using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Jobs;

/// <summary>
/// Runs <c>outbox_jobs</c> (spec §4): each job is claimed with <c>FOR UPDATE SKIP LOCKED</c> and runs in
/// its own transaction, which also marks it <c>done</c>. A failed job is retried with backoff and marked
/// <c>failed</c> after <see cref="MaxAttempts"/>. Each handler is idempotent by its ledger key.
/// </summary>
public sealed partial class OutboxProcessor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OutboxProcessor> logger)
{
    public const int MaxAttempts = 10;
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>Runs due jobs until none is left or <paramref name="max"/> ran. Returns the number of jobs run.</summary>
    public async Task<int> RunOnceAsync(int max, CancellationToken cancellationToken)
    {
        var ran = 0;
        while (ran < max && await RunNextAsync(cancellationToken))
        {
            ran++;
        }

        return ran;
    }

    private async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        var now = clock.UtcNowForDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.OutboxJobs
            .FromSql($"""
                SELECT * FROM vybe.outbox_jobs
                WHERE status = 'pending' AND run_after <= {now}
                ORDER BY run_after, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        try
        {
            var runAgainAt = job.Kind switch
            {
                ChallengeResolvedJob.Kind => await ChallengeResolvedJob.RunAsync(
                    db,
                    scope.ServiceProvider.GetRequiredService<AchievementCatalog>(),
                    JsonSerializer.Deserialize<ChallengeResolvedJob.Payload>(job.Payload, PayloadJson) ?? throw new InvalidDataException("empty payload"),
                    now,
                    cancellationToken),
                AchievementSyncJob.Kind => await AchievementSyncJob.RunAsync(
                    db,
                    scope.ServiceProvider.GetRequiredService<AchievementCatalog>(),
                    JsonSerializer.Deserialize<AchievementSyncJob.Payload>(job.Payload, PayloadJson) ?? throw new InvalidDataException("empty payload"),
                    now,
                    cancellationToken),
                _ => throw new InvalidOperationException($"unknown outbox job kind {job.Kind}"),
            };

            if (runAgainAt is { } at)
            {
                // Not a failure: the job waits (e.g. for a suspension to end) without using an attempt.
                job.RunAfter = at;
            }
            else
            {
                job.Status = OutboxStatus.Done;
                job.Attempts++;
            }

            job.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var attempts = job.Attempts + 1;
            var status = attempts >= MaxAttempts ? "failed" : "pending";
            var retryAt = now.AddSeconds(Math.Min(3600, Math.Pow(2, attempts)));
            var error = $"{exception.GetType().Name}: {exception.Message}";
            error = error[..Math.Min(error.Length, 500)];
            await db.Database.ExecuteSqlAsync($"""
                UPDATE vybe.outbox_jobs
                SET attempts = {attempts}, status = {status}, run_after = {retryAt}, last_error = {error}
                WHERE id = {job.Id}
                """, cancellationToken);
            if (status == "failed")
            {
                LogJobGaveUp(logger, exception, job.JobKey, attempts);
            }
            else
            {
                LogJobFailed(logger, exception, job.JobKey, attempts);
            }
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox job {JobKey} is failed after {Attempts} attempts; it will not run again")]
    private static partial void LogJobGaveUp(ILogger logger, Exception exception, string jobKey, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox job {JobKey} failed (attempt {Attempts})")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string jobKey, int attempts);
}
