using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Jobs;

/// <summary>
/// Deadline sweeps (spec §4, §6). Every write is a status compare-and-swap on rows claimed with
/// <c>SKIP LOCKED</c>, so a sweep never waits for a request and a repeated sweep changes nothing.
/// A row that a request holds is left for the next sweep.
/// </summary>
public sealed class Sweeper(IServiceScopeFactory scopes, TimeProvider clock)
{
    public const int BatchSize = 500;

    public sealed record Result(int Sessions, int Challenges, int Suspensions);

    public async Task<Result> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        var now = clock.UtcNowForDb();
        return new Result(
            await RepeatAsync(() => ExpireSessionsAsync(db, now, cancellationToken)),
            await RepeatAsync(() => ExpireChallengesAsync(db, now, cancellationToken)),
            await RepeatAsync(() => ResetSuspensionsAsync(db, now, cancellationToken)));
    }

    private static async Task<int> RepeatAsync(Func<Task<int>> batch)
    {
        int total = 0, count;
        do
        {
            count = await batch();
            total += count;
        }
        while (count == BatchSize);
        return total;
    }

    /// <summary>
    /// <c>created</c> past <c>start_deadline</c> and <c>active</c> past <c>submit_deadline</c> become
    /// <c>expired</c> (the same state a late submit writes). An expired recipient session moves its
    /// challenge <c>accepted -> expired</c> in the same statement.
    /// </summary>
    private static async Task<int> ExpireSessionsAsync(VybeDbContext db, DateTime now, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<int>($"""
            WITH due AS (
                SELECT id FROM vybe.game_sessions
                WHERE (status = 'created' AND start_deadline < {now}) OR (status = 'active' AND submit_deadline < {now})
                ORDER BY id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED),
            expired AS (
                UPDATE vybe.game_sessions s SET status = 'expired'
                FROM due
                WHERE s.id = due.id
                  AND ((s.status = 'created' AND s.start_deadline < {now}) OR (s.status = 'active' AND s.submit_deadline < {now}))
                RETURNING s.id),
            challenges AS (
                UPDATE vybe.challenges c SET status = 'expired'
                FROM expired
                WHERE c.recipient_session_id = expired.id AND c.status = 'accepted')
            SELECT count(*)::int AS "Value" FROM expired
            """).ToListAsync(cancellationToken)).Single();

    /// <summary>
    /// <c>open</c> past <c>accept_deadline</c>; <c>accepted</c> past <c>complete_deadline</c> only without a
    /// recipient session, or with one that is <c>created</c>, <c>rejected</c> or <c>expired</c>. A recipient
    /// session that a request holds (start, submit) is skipped, so a run that becomes active is never cut off.
    /// </summary>
    private static async Task<int> ExpireChallengesAsync(VybeDbContext db, DateTime now, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<int>($"""
            WITH due AS (
                SELECT c.id FROM vybe.challenges c
                WHERE (c.status = 'open' AND c.accept_deadline <= {now})
                   OR (c.status = 'accepted' AND c.complete_deadline <= {now}
                       AND (c.recipient_session_id IS NULL
                            OR EXISTS (SELECT 1 FROM vybe.game_sessions s
                                       WHERE s.id = c.recipient_session_id AND s.status IN ('created', 'rejected', 'expired')
                                       FOR SHARE SKIP LOCKED)))
                ORDER BY c.id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED),
            expired AS (
                UPDATE vybe.challenges c SET status = 'expired'
                FROM due
                WHERE c.id = due.id AND c.status IN ('open', 'accepted')
                RETURNING c.id)
            SELECT count(*)::int AS "Value" FROM expired
            """).ToListAsync(cancellationToken)).Single();

    /// <summary>An elapsed suspension is treated as active on read and reset here (spec §4 players).</summary>
    private static async Task<int> ResetSuspensionsAsync(VybeDbContext db, DateTime now, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<int>($"""
            WITH due AS (
                SELECT id FROM vybe.players
                WHERE status = 'suspended' AND suspended_until IS NOT NULL AND suspended_until <= {now}
                ORDER BY id
                LIMIT {BatchSize}
                FOR NO KEY UPDATE SKIP LOCKED),
            reset AS (
                UPDATE vybe.players p
                SET status = 'active', suspended_until = NULL, suspension_reason = NULL, updated_at = {now}
                FROM due
                WHERE p.id = due.id AND p.status = 'suspended' AND p.suspended_until <= {now}
                RETURNING p.id)
            SELECT count(*)::int AS "Value" FROM reset
            """).ToListAsync(cancellationToken)).Single();
}
