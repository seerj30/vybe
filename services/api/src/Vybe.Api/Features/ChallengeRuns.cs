using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

/// <summary>The recipient run of a challenge (spec §5 createStreetHoopsSession, §6 Challenge, invariants 10 and 12).</summary>
public static class ChallengeRuns
{
    public const string ResolvedJobKind = "challenge-resolved";

    public static string ResolvedJobKey(Guid challengeId, Guid playerId) => $"challenge-resolved:{challengeId}:{playerId}";

    /// <summary>The players who blocked the other one, in either direction (empty when no block exists).</summary>
    public static async Task<List<Guid>> BlockersAsync(VybeDbContext db, Guid a, Guid b, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.PlayerBlocks.AsNoTracking()
            .Where(x => (x.BlockerPlayerId == a && x.BlockedPlayerId == b) || (x.BlockerPlayerId == b && x.BlockedPlayerId == a))
            .Select(x => x.BlockerPlayerId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Checks that the caller may open the recipient session and locks the challenge row; returns the
    /// challenge and the creator run's seed. Takes the pair lock first and re-checks blocks inside it.
    /// </summary>
    public static async Task<(Challenge Challenge, long Seed)> LockForRecipientAsync(
        VybeDbContext db, Guid playerId, Guid challengeId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var unavailable = Problem.Of(404, "challenge_unavailable");
        var found = await db.Challenges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == challengeId, cancellationToken);
        if (found is null || found.RecipientPlayerId != playerId || found.Suppressed)
        {
            throw unavailable;
        }

        await PairLock.AcquireAsync(db, playerId, found.CreatorPlayerId, cancellationToken);
        if ((await BlockersAsync(db, playerId, found.CreatorPlayerId, cancellationToken)).Count > 0)
        {
            throw unavailable;
        }

        var challenge = await db.Challenges
            .FromSql($"SELECT * FROM vybe.challenges WHERE id = {challengeId} FOR UPDATE")
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        if (challenge.RecipientSessionId is not null)
        {
            throw Problem.Of(409, "challenge_session_exists");
        }

        if (challenge.Status == ChallengeStatus.Open)
        {
            throw unavailable; // a preset recipient who has not accepted yet is not bound
        }

        if (challenge.Status != ChallengeStatus.Accepted || now >= challenge.CompleteDeadline)
        {
            throw Problem.Of(410, "challenge_expired");
        }

        var seed = await db.GameSessions.Where(s => s.Id == challenge.CreatorSessionId).Select(s => s.Seed).SingleAsync(cancellationToken);
        return (challenge, seed);
    }

    /// <summary>Binds the new recipient session by compare-and-swap (spec §5).</summary>
    public static async Task BindSessionAsync(VybeDbContext db, Guid challengeId, Guid sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var bound = await db.Challenges
            .Where(c => c.Id == challengeId && c.Status == ChallengeStatus.Accepted && c.RecipientSessionId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RecipientSessionId, sessionId), cancellationToken);
        if (bound != 1)
        {
            throw Problem.Of(409, "challenge_session_exists");
        }
    }

    /// <summary>A recipient session that ends <c>rejected</c> or <c>expired</c> moves its challenge <c>accepted -> expired</c>.</summary>
    public static async Task ExpireForRecipientSessionAsync(VybeDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Challenges
            .Where(c => c.RecipientSessionId == sessionId && c.Status == ChallengeStatus.Accepted)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ChallengeStatus.Expired), cancellationToken);
    }

    /// <summary>
    /// A verified recipient run of any length resolves the challenge by compare-and-swap
    /// <c>accepted -> completed</c> and queues one outbox job per participant. If the challenge is no
    /// longer accepted (e.g. cancelled by a block), nothing changes. Only this player's rows are locked:
    /// the creator's run is read without a lock and the creator's work runs in the outbox (invariant 10).
    /// </summary>
    public static async Task ResolveAsync(VybeDbContext db, GameSession session, int score, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);
        var challenge = await db.Challenges.AsNoTracking()
            .Where(c => c.Id == session.ChallengeId && c.RecipientSessionId == session.Id)
            .Select(c => new { c.Id, c.CreatorPlayerId, c.CreatorSessionId })
            .SingleAsync(cancellationToken);
        var creatorScore = await db.GameSessions.Where(s => s.Id == challenge.CreatorSessionId).Select(s => s.Score ?? 0)
            .SingleAsync(cancellationToken);
        // Ties: the creator keeps the title for display only; no winner is stored (spec §2, §7).
        Guid? winner = score > creatorScore ? session.PlayerId : score < creatorScore ? challenge.CreatorPlayerId : null;

        var resolved = await db.Challenges
            .Where(c => c.Id == challenge.Id && c.Status == ChallengeStatus.Accepted)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, ChallengeStatus.Completed)
                .SetProperty(c => c.WinnerPlayerId, winner)
                .SetProperty(c => c.ResolvedAt, now), cancellationToken);
        if (resolved == 0)
        {
            return;
        }

        foreach (var playerId in new[] { challenge.CreatorPlayerId, session.PlayerId })
        {
            var key = ResolvedJobKey(challenge.Id, playerId);
            var payload = JsonSerializer.Serialize(new { challengeId = challenge.Id, playerId });
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vybe.outbox_jobs (id, kind, job_key, payload, status, attempts, run_after)
                VALUES ({Guid.CreateVersion7()}, {ResolvedJobKind}, {key}, {payload}::jsonb, 'pending', 0, {now})
                ON CONFLICT (job_key) DO NOTHING
                """, cancellationToken);
        }
    }
}
