using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Features;

public sealed record FlaggedRun(
    Guid SessionId, PlayerRef Player, IReadOnlyList<string> Flags, int Score, int EventCount, DateTime StartedAt, DateTime SubmittedAt,
    long RunMs, long RewardCoins, Guid? ChallengeId);

/// <summary>
/// Review flags for moderators (spec §7, §17 decision 4: flags only, no automatic action). A read: it writes no
/// audit row; the actions that follow a review (suspension, report resolution) are audited where they happen.
/// </summary>
public static class ReviewFlagEndpoints
{
    public static readonly IReadOnlyList<string> Known =
        [ReviewFlags.PerfectRateHigh, ReviewFlags.GapVarianceLow, ReviewFlags.AimVarianceLow, ReviewFlags.DailyCapStreak, ReviewFlags.TimingSpreadLow];

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGroup("/admin").RequireAuthorization(p => AdminAccess.Require(p, AdminRole.Moderator))
            .MapGet("/flagged-runs", ListAsync);
    }

    /// <summary>
    /// Verified runs with at least one flag, newest first. Runs of deleting and deleted players are left out: their
    /// handle is gone and no moderator action applies to them.
    /// </summary>
    private static async Task<Page<FlaggedRun>> ListAsync(
        string? flag, string? cursor, int? limit, VybeDbContext db, CancellationToken cancellationToken)
    {
        var take = Cursor.Limit(limit, 20);
        if (flag is not null && !Known.Contains(flag))
        {
            throw Problem.ValidationFailed("/flag", "invalid", "unknown review flag");
        }

        var query = db.GameSessions.AsNoTracking()
            .Where(s => s.Status == GameSessionStatus.Verified && s.ReviewFlags.Length > 0);
        if (flag is not null)
        {
            query = query.Where(s => s.ReviewFlags.Contains(flag));
        }

        if (Cursor.Decode(cursor) is { } decoded)
        {
            var (verified, id) = AdminEndpoints.ParseTimeCursor(decoded);
            query = query.Where(s => s.VerifiedAt < verified || (s.VerifiedAt == verified && s.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .Join(
                db.Players.Where(p => p.Status != PlayerStatus.Deleting && p.Status != PlayerStatus.Deleted),
                s => s.PlayerId, p => p.Id, (s, p) => new { Session = s, p.Handle })
            .OrderByDescending(r => r.Session.VerifiedAt).ThenByDescending(r => r.Session.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var items = page.Select(r => new FlaggedRun(
            r.Session.Id, new PlayerRef(r.Session.PlayerId, r.Handle), r.Session.ReviewFlags, r.Session.Score!.Value, r.Session.EventCount!.Value,
            r.Session.StartedAt!.Value, r.Session.SubmittedAt!.Value,
            (long)(r.Session.SubmittedAt!.Value - r.Session.StartedAt!.Value).TotalMilliseconds, r.Session.RewardCoins, r.Session.ChallengeId))
            .ToList();
        var last = page.Count > 0 ? page[^1].Session : null;
        return new Page<FlaggedRun>(items, rows.Count > take ? AdminEndpoints.TimeCursor(last!.VerifiedAt!.Value, last.Id) : null);
    }
}
