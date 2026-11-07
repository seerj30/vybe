using Microsoft.EntityFrameworkCore;
using Vybe.Data;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Deletes <c>idempotency_records</c> after <c>expires_at</c> (created + 72 h, spec §4). A record whose lease
/// is still held is kept. Rows are claimed with <c>SKIP LOCKED</c> in batches, so the job never waits for a
/// request and several instances can run it. Same pattern as the jobs in <c>Jobs/</c> (ADR 0004 #6–7).
/// Times come from the database clock, like <c>expires_at</c> and <c>locked_until</c>.
/// </summary>
public sealed class IdempotencyCleanup(IServiceScopeFactory scopes)
{
    public const int BatchSize = 1000;

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        int total = 0, count;
        do
        {
            count = (await db.Database.SqlQuery<int>($"""
                WITH due AS (
                    SELECT actor_subject, operation, key FROM vybe.idempotency_records
                    WHERE expires_at < now() AND (status = 'completed' OR locked_until < now())
                    ORDER BY expires_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED),
                deleted AS (
                    DELETE FROM vybe.idempotency_records r
                    USING due
                    WHERE r.actor_subject = due.actor_subject AND r.operation = due.operation AND r.key = due.key
                    RETURNING 1)
                SELECT count(*)::int AS "Value" FROM deleted
                """).ToListAsync(cancellationToken)).Single();
            total += count;
        }
        while (count == BatchSize);
        return total;
    }
}

internal sealed partial class IdempotencyCleanupWorker(IdempotencyCleanup cleanup, IConfiguration configuration, ILogger<IdempotencyCleanupWorker> logger)
    : BackgroundService
{
    public const string IntervalKey = "Jobs:IdempotencyCleanupSeconds";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue(IntervalKey, 3600)));
        do
        {
            try
            {
                var deleted = await cleanup.RunOnceAsync(stoppingToken);
                if (deleted > 0)
                {
                    LogDeleted(logger, deleted);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired idempotency records")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Idempotency cleanup failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
