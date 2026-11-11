using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Data;
using Vybe.Data.Entities;
using Vybe.GameRules;

namespace Vybe.Api.Features;

/// <summary>
/// The seed step for achievements (deploy <c>seed</c> / <c>release</c>, Development, tests): definitions, then the backfill.
/// The backfill runs on every seed, not only when a definition changed: it is idempotent, and a backfill that stopped
/// half way (failed deploy) then completes on the next seed. Small data in the beta; revisit at about 10,000 players.
/// </summary>
public static class AchievementSeeding
{
    public static async Task SeedAndBackfillAsync(VybeDbContext db, AchievementCatalog catalog, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        await AchievementSeeder.SeedAsync(db, catalog, cancellationToken);
        await AchievementBackfill.RunAsync(db, catalog, Infrastructure.Clock.UtcNowForDb(clock), cancellationToken);
    }
}

/// <summary>
/// Idempotent seeding of <c>assets/manifests/achievements.v1.json</c> into <c>achievements</c> (Development, tests, deploy
/// <c>seed</c> step), like the quests. A code that left the file becomes inactive; its unlocks stay.
/// </summary>
public static class AchievementSeeder
{
    /// <summary>Returns true when a definition was added, reactivated or got a new rule.</summary>
    public static async Task<bool> SeedAsync(VybeDbContext db, AchievementCatalog catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(catalog);
        var existing = await db.Achievements.ToListAsync(cancellationToken);
        var changed = false;
        foreach (var row in existing)
        {
            row.IsActive = catalog.Find(row.Code) is not null;
        }

        foreach (var a in catalog.All)
        {
            var criteria = JsonSerializer.Serialize(new { stat = a.Stat, target = a.Target });
            var row = existing.SingleOrDefault(r => r.Code == a.Code);
            if (row is null)
            {
                row = new Achievement { Id = Guid.CreateVersion7(), Code = a.Code, Criteria = criteria };
                db.Achievements.Add(row);
                changed = true;
            }
            else if (!db.Entry(row).Property(r => r.IsActive).OriginalValue || row.Version != a.Version || !SameCriteria(row.Criteria, a))
            {
                changed = true;
            }

            row.Criteria = criteria;
            row.Version = a.Version;
            row.RewardXp = a.RewardXp;
            row.IsActive = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    private static bool SameCriteria(string json, AchievementContent a)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.TryGetProperty("stat", out var stat) && stat.GetString() == a.Stat
            && root.TryGetProperty("target", out var target) && target.TryGetInt32(out var t) && t == a.Target;
    }
}

/// <summary>
/// One computation of every stat from the stored data, for players who played before the achievements existed
/// (docs/design/p1-achievements.md, Backfill). Small data: one transaction per player with the player row lock.
/// Writes keep the higher value, so a second run changes nothing.
/// </summary>
public static class AchievementBackfill
{
    private sealed record RunRow(string GameId, string RulesetId, int? EventCount, DateTime? StartedAt, DateTime? SubmittedAt, int? Score,
        string[] ReviewFlags, Guid? ChallengeId);

    /// <summary>Returns the number of players computed.</summary>
    public static async Task<int> RunAsync(VybeDbContext db, AchievementCatalog catalog, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var players = await db.Players.AsNoTracking()
            .Where(p => p.Status != PlayerStatus.Deleting && p.Status != PlayerStatus.Deleted)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        var configs = new Dictionary<string, IRewardConfig>(StringComparer.Ordinal);
        foreach (var playerId in players)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // The row lock waits for a running anonymization; a player deleted since the list gets no new rows (J4).
            var status = await db.Players.FromSql($"SELECT * FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE")
                .AsNoTracking().Select(p => p.Status).SingleAsync(cancellationToken);
            if (status is PlayerStatus.Deleting or PlayerStatus.Deleted)
            {
                continue;
            }

            await RunsAsync(db, playerId, configs, now, cancellationToken);
            await AchievementProgress.RaiseAsync(db, playerId, AchievementStats.PlayStreak, await LongestStreakAsync(db, playerId, cancellationToken),
                now, cancellationToken);
            await WeeklyAsync(db, playerId, now, cancellationToken);
            await AchievementProgress.SyncCountsAsync(db, catalog, playerId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        return players.Count;
    }

    /// <summary><c>runs</c>, <c>runs:{gameId}</c>, <c>best:{gameId}</c> and <c>games_played</c> with the live rules.</summary>
    private static async Task RunsAsync(
        VybeDbContext db, Guid playerId, Dictionary<string, IRewardConfig> configs, DateTime now, CancellationToken cancellationToken)
    {
        var sessions = await db.GameSessions.AsNoTracking()
            .Where(s => s.PlayerId == playerId && s.Status == GameSessionStatus.Verified)
            .Select(s => new RunRow(s.GameId, s.RulesetId, s.EventCount, s.StartedAt, s.SubmittedAt, s.Score, s.ReviewFlags, s.ChallengeId))
            .ToListAsync(cancellationToken);
        var eligible = new List<RunRow>();
        foreach (var s in sessions.Where(s => s.StartedAt is not null && s.SubmittedAt is not null && Games.IsKnown(s.GameId)))
        {
            if (!configs.TryGetValue(s.RulesetId, out var config))
            {
                var json = await db.GameRulesets.Where(r => r.Id == s.RulesetId).Select(r => r.Config).SingleAsync(cancellationToken);
                config = Games.For(s.GameId).ParseConfig(json);
                configs[s.RulesetId] = config;
            }

            if (Rewards.Ineligibility(config, s.EventCount ?? 0, s.StartedAt!.Value, s.SubmittedAt!.Value) is null)
            {
                eligible.Add(s);
            }
        }

        await AchievementProgress.RaiseAsync(db, playerId, AchievementStats.Runs, eligible.Count, now, cancellationToken);
        foreach (var game in eligible.GroupBy(s => s.GameId))
        {
            await AchievementProgress.RaiseAsync(db, playerId, AchievementStats.RunsOf(game.Key), game.Count(), now, cancellationToken);
            // Same rule as the leaderboard (LeaderboardEndpoints.Counts): no review flags, not a challenge recipient run.
            var best = game.Where(s => s.ChallengeId is null && s.ReviewFlags.Length == 0).Select(s => s.Score ?? 0).DefaultIfEmpty(0).Max();
            await AchievementProgress.RaiseAsync(db, playerId, AchievementStats.BestOf(game.Key), best, now, cancellationToken);
        }

        await AchievementProgress.RaiseAsync(db, playerId, AchievementStats.GamesPlayed, eligible.Select(s => s.GameId).Distinct().Count(), now,
            cancellationToken);
    }

    private static async Task<int> LongestStreakAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken)
    {
        var days = await db.PlayerDailyCounters.AsNoTracking()
            .Where(c => c.PlayerId == playerId && c.RunsRewarded > 0)
            .OrderBy(c => c.Day)
            .Select(c => c.Day)
            .ToListAsync(cancellationToken);
        int longest = 0, current = 0;
        for (var i = 0; i < days.Count; i++)
        {
            current = i > 0 && days[i] == days[i - 1].AddDays(1) ? current + 1 : 1;
            longest = Math.Max(longest, current);
        }

        return Math.Min(longest, AchievementStats.StreakLookbackDays);
    }

    private static async Task WeeklyAsync(VybeDbContext db, Guid playerId, DateTime now, CancellationToken cancellationToken)
    {
        var weeks = await db.WeeklyLeaderboardScores.AsNoTracking().Where(s => s.PlayerId == playerId)
            .Select(s => new { s.GameId, s.WeekStart })
            .ToListAsync(cancellationToken);
        var bestRank = int.MaxValue;
        foreach (var week in weeks)
        {
            bestRank = Math.Min(bestRank, await AchievementProgress.WeeklyRankAsync(db, playerId, week.GameId, week.WeekStart, now, cancellationToken));
        }

        if (bestRank <= 10)
        {
            await AchievementProgress.RaiseAsync(db, playerId, AchievementStats.WeeklyTop10, 1, now, cancellationToken);
        }

        if (bestRank == 1)
        {
            await AchievementProgress.RaiseAsync(db, playerId, AchievementStats.WeeklyTop1, 1, now, cancellationToken);
        }
    }
}
