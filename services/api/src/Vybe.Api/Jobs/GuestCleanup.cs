using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Jobs;

/// <summary>
/// Spec §17 decision 2 and 8: a guest player whose last guest session ended <see cref="Grace"/> ago gets a queued
/// deletion request, exactly like J4 (status <c>deleting</c>, then <see cref="Features.PrivacyProcessor"/>
/// anonymizes it). Nobody can sign in as that guest any more, so nobody else can ask for the deletion.
/// A guest needs at least one session (a guest without one is not "expired", only broken). A suspended guest
/// with an open report stays until a moderator resolves the report, so the moderation context is kept.
/// Players are claimed with <c>SKIP LOCKED</c>, and the conditions are checked again in the update, so a
/// repeated or parallel run changes nothing twice.
/// </summary>
public sealed class GuestCleanup(IServiceScopeFactory scopes, TimeProvider clock)
{
    public const int BatchSize = 500;

    /// <summary>Time between the end of the last guest session and the deletion request.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(30);

    /// <summary>Queues the deletion of every due guest; returns the number of new deletion requests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        var now = clock.UtcNowForDb();
        var cutoff = now - Grace;
        int total = 0, count;
        do
        {
            // LEAST ignores NULL: a session ends at revocation or at expiry, whichever is first.
            count = (await db.Database.SqlQuery<int>($"""
                WITH due AS (
                    SELECT p.id FROM vybe.players p
                    WHERE p.kind = 'guest' AND p.status IN ('active', 'suspended')
                      AND EXISTS (SELECT 1 FROM vybe.guest_sessions s WHERE s.player_id = p.id)
                      AND NOT EXISTS (SELECT 1 FROM vybe.guest_sessions s
                                      WHERE s.player_id = p.id AND LEAST(s.revoked_at, s.expires_at) > {cutoff})
                      AND NOT (p.status = 'suspended'
                               AND EXISTS (SELECT 1 FROM vybe.player_reports r WHERE r.target_player_id = p.id AND r.status = 'open'))
                    ORDER BY p.id
                    LIMIT {BatchSize}
                    FOR NO KEY UPDATE SKIP LOCKED),
                marked AS (
                    UPDATE vybe.players p SET status = 'deleting', updated_at = {now}
                    FROM due
                    WHERE p.id = due.id AND p.kind = 'guest' AND p.status IN ('active', 'suspended')
                    RETURNING p.id),
                queued AS (
                    INSERT INTO vybe.deletion_requests (id, player_id, status, created_at)
                    SELECT gen_random_uuid(), marked.id, 'queued', {now} FROM marked
                    RETURNING id)
                SELECT count(*)::int AS "Value" FROM queued
                """).ToListAsync(cancellationToken)).Single();
            total += count;
        }
        while (count == BatchSize);
        return total;
    }
}

/// <summary>Runs <see cref="GuestCleanup"/> every <c>Jobs:GuestCleanupSeconds</c> (default one hour) unless jobs are off.</summary>
internal sealed partial class GuestCleanupWorker(GuestCleanup cleanup, IConfiguration configuration, ILogger<GuestCleanupWorker> logger)
    : BackgroundService
{
    public const string IntervalKey = "Jobs:GuestCleanupSeconds";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue(IntervalKey, 3600))));
        do
        {
            try
            {
                var queued = await cleanup.RunOnceAsync(stoppingToken);
                if (queued > 0)
                {
                    LogQueued(logger, queued);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Guest cleanup queued {Count} expired guests for anonymization")]
    private static partial void LogQueued(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Guest cleanup failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
