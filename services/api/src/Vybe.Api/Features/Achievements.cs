using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public enum AchievementTier { Bronze, Silver, Gold }

/// <summary>One definition of <c>assets/manifests/achievements.v1.json</c>.</summary>
public sealed record AchievementContent(
    string Code, int Version, string Category, AchievementTier Tier, string Name, string Description, string Stat, int Target, int RewardXp);

public sealed record AchievementFile(string? Description, int Version, AchievementContent[] Achievements);

/// <summary>An achievement that a transaction just unlocked (submit result and toast).</summary>
public sealed record AchievementUnlocked(string Code, string Name, AchievementTier Tier, int RewardXp);

/// <summary>The achievement definitions, loaded once from the manifest (docs/design/p1-achievements.md).</summary>
public sealed class AchievementCatalog
{
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly Dictionary<string, AchievementContent> byCode;

    public AchievementCatalog(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "assets", "manifests", "achievements.v1.json");
        var file = JsonSerializer.Deserialize<AchievementFile>(File.ReadAllText(path), FileJson)
            ?? throw new InvalidDataException($"content file {path} is empty");
        foreach (var a in file.Achievements)
        {
            if (!AchievementStats.IsKnown(a.Stat) || a.Target < 1 || a.RewardXp < 0 || a.Version < 1
                || string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Code))
            {
                throw new InvalidDataException($"achievement {a.Code}: invalid definition");
            }
        }

        All = file.Achievements;
        byCode = file.Achievements.ToDictionary(a => a.Code, StringComparer.Ordinal);
    }

    /// <summary>In manifest order (category, then tier).</summary>
    public IReadOnlyList<AchievementContent> All { get; }

    public AchievementContent? Find(string code) => byCode.GetValueOrDefault(code);

    public int Order(string code)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i].Code == code)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}

/// <summary>The stats that achievements count. A definition names one stat; a new stat needs code here.</summary>
public static class AchievementStats
{
    public const string Runs = "runs";
    public const string GamesPlayed = "games_played";
    public const string PlayStreak = "play_streak";
    public const string WeeklyTop10 = "weekly_top10";
    public const string WeeklyTop1 = "weekly_top1";
    public const string ChallengesCompleted = "challenges_completed";
    public const string ChallengesWon = "challenges_won";
    public const string RematchesCompleted = "rematches_completed";
    public const string QuestsClaimed = "quests_claimed";
    public const string OutfitsSaved = "outfits_saved";
    public const string Friends = "friends";
    public const string ItemsBought = "items_bought";

    /// <summary>Days looked back for the play streak; the stat never goes above it.</summary>
    public const int StreakLookbackDays = 60;

    private static readonly HashSet<string> Fixed =
    [
        Runs, GamesPlayed, PlayStreak, WeeklyTop10, WeeklyTop1, ChallengesCompleted, ChallengesWon, RematchesCompleted,
        QuestsClaimed, OutfitsSaved, Friends, ItemsBought,
    ];

    public static string RunsOf(string gameId) => $"runs:{gameId}";

    public static string BestOf(string gameId) => $"best:{gameId}";

    public static bool IsKnown(string stat) =>
        Fixed.Contains(stat)
        || (stat.StartsWith("runs:", StringComparison.Ordinal) && Games.IsKnown(stat[5..]))
        || (stat.StartsWith("best:", StringComparison.Ordinal) && Games.IsKnown(stat[5..]));
}

/// <summary>
/// P1 achievements (docs/design/p1-achievements.md). Progress is computed on the server only, inside the transactions
/// that change the counted data, after their own locks. Every write locks rows of one player only (invariant 10):
/// the caller holds that player's row lock (<c>FOR NO KEY UPDATE</c>). Stats use atomic upserts (invariant 11);
/// unlocks use the primary key of <c>player_achievements</c>, so a replay or a retry unlocks and pays nothing twice.
/// </summary>
public static class AchievementProgress
{
    /// <summary>
    /// The stats of one verified, reward-eligible run, then the unlock pass. <paramref name="counts"/>: the run counts for the
    /// leaderboard (no review flags, not a challenge recipient run), so its score counts for score achievements.
    /// <paramref name="runNumber"/>: the run's number of the day (1 = the first, which can extend the play streak).
    /// </summary>
    public static async Task<IReadOnlyList<AchievementUnlocked>> RecordRunAsync(
        VybeDbContext db, AchievementCatalog catalog, GameSession session, bool counts, int runNumber, DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);
        var playerId = session.PlayerId;
        await AddAsync(db, playerId, AchievementStats.Runs, 1, now, cancellationToken);
        await AddAsync(db, playerId, AchievementStats.RunsOf(session.GameId), 1, now, cancellationToken);
        await RaiseAsync(db, playerId, AchievementStats.GamesPlayed, await GamesPlayedAsync(db, playerId, cancellationToken), now, cancellationToken);
        if (runNumber == 1)
        {
            var streak = await StreakEndingAsync(db, playerId, session.RewardDay!.Value, cancellationToken);
            await RaiseAsync(db, playerId, AchievementStats.PlayStreak, streak, now, cancellationToken);
        }

        if (counts)
        {
            await RaiseAsync(db, playerId, AchievementStats.BestOf(session.GameId), session.Score ?? 0, now, cancellationToken);
            var rank = await WeeklyRankAsync(db, playerId, session.GameId, LeaderboardEndpoints.WeekStart(session.StartedAt!.Value), now,
                cancellationToken);
            if (rank <= WeeklyRankLimit)
            {
                await RaiseAsync(db, playerId, AchievementStats.WeeklyTop10, 1, now, cancellationToken);
            }

            if (rank == 1)
            {
                await RaiseAsync(db, playerId, AchievementStats.WeeklyTop1, 1, now, cancellationToken);
            }
        }

        return await UnlockAsync(db, catalog, playerId, now, cancellationToken);
    }

    /// <summary>
    /// Recomputes every count stat (challenges, quests, outfits, friends, items) from the source rows, keeps the higher
    /// value, then runs the unlock pass. Idempotent. The caller holds the player row lock.
    /// </summary>
    public static async Task<IReadOnlyList<AchievementUnlocked>> SyncCountsAsync(
        VybeDbContext db, AchievementCatalog catalog, Guid playerId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var completed = db.Challenges.AsNoTracking()
            .Where(c => c.Status == ChallengeStatus.Completed && (c.CreatorPlayerId == playerId || c.RecipientPlayerId == playerId));
        var counts = new (string Stat, long Value)[]
        {
            (AchievementStats.ChallengesCompleted, await completed.CountAsync(cancellationToken)),
            (AchievementStats.ChallengesWon, await completed.CountAsync(c => c.WinnerPlayerId == playerId, cancellationToken)),
            (AchievementStats.RematchesCompleted, await completed.CountAsync(c => c.RematchOfChallengeId != null, cancellationToken)),
            (AchievementStats.QuestsClaimed, await db.PlayerQuests.CountAsync(q => q.PlayerId == playerId && q.Status == QuestStatus.Claimed, cancellationToken)),
            (AchievementStats.OutfitsSaved, await db.SavedOutfits.CountAsync(o => o.PlayerId == playerId, cancellationToken)),
            (AchievementStats.Friends, await db.Friendships.CountAsync(
                f => f.Status == FriendshipStatus.Accepted && (f.RequesterPlayerId == playerId || f.AddresseePlayerId == playerId), cancellationToken)),
            (AchievementStats.ItemsBought, await db.PlayerInventory.CountAsync(
                i => i.PlayerId == playerId && i.Source == InventorySource.Purchase, cancellationToken)),
        };
        foreach (var (stat, value) in counts)
        {
            await RaiseAsync(db, playerId, stat, value, now, cancellationToken);
        }

        return await UnlockAsync(db, catalog, playerId, now, cancellationToken);
    }

    /// <summary>
    /// Unlocks every active achievement whose stat reached its target, in code order, and grants their XP once (level
    /// coins as for quests, spec §8). Nothing for a suspended, deleting or deleted player: the stats stay, and the next
    /// pass after the suspension unlocks them (spec §6: suspension blocks economic actions).
    /// </summary>
    public static async Task<IReadOnlyList<AchievementUnlocked>> UnlockAsync(
        VybeDbContext db, AchievementCatalog catalog, Guid playerId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(catalog);
        // Tracked: AddXpAsync changes the level. The caller holds the row lock, so this read is current.
        var player = await db.Players.SingleAsync(p => p.Id == playerId, cancellationToken);
        if (player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted || CurrentPlayer.IsSuspended(player, now))
        {
            return [];
        }

        var rows = await db.Database.SqlQuery<UnlockRow>($"""
            WITH unlocked AS (
                INSERT INTO vybe.player_achievements (player_id, achievement_id, unlocked_at)
                SELECT s.player_id, a.id, {now}
                FROM vybe.achievements a
                JOIN vybe.player_stats s ON s.player_id = {playerId} AND s.stat = a.criteria->>'stat'
                WHERE a.is_active AND s.value >= (a.criteria->>'target')::bigint
                ORDER BY a.code
                ON CONFLICT DO NOTHING
                RETURNING achievement_id)
            SELECT a.code, a.reward_xp FROM unlocked u JOIN vybe.achievements a ON a.id = u.achievement_id ORDER BY a.code
            """).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        await RewardService.AddXpAsync(db, player, rows.Sum(r => r.RewardXp), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToUnlocked(catalog, rows);
    }

    /// <summary>What one verified run unlocked: its transaction is the only writer of <c>unlocked_at = verified_at</c>.</summary>
    public static async Task<IReadOnlyList<AchievementUnlocked>> UnlockedByRunAsync(
        VybeDbContext db, AchievementCatalog catalog, GameSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);
        if (session.VerifiedAt is not { } verifiedAt)
        {
            return [];
        }

        var rows = await db.PlayerAchievements.AsNoTracking()
            .Where(p => p.PlayerId == session.PlayerId && p.UnlockedAt == verifiedAt)
            .Join(db.Achievements, p => p.AchievementId, a => a.Id, (_, a) => new { a.Code, a.RewardXp })
            .ToListAsync(cancellationToken);
        return ToUnlocked(catalog, rows.Select(r => new UnlockRow(r.Code, r.RewardXp)));
    }

    /// <summary><c>value = value + delta</c> in one statement (invariant 11).</summary>
    public static async Task AddAsync(VybeDbContext db, Guid playerId, string stat, long delta, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.player_stats (player_id, stat, value, updated_at) VALUES ({playerId}, {stat}, {delta}, {now})
            ON CONFLICT (player_id, stat) DO UPDATE SET value = player_stats.value + excluded.value, updated_at = excluded.updated_at
            """, cancellationToken);
    }

    /// <summary><c>value = GREATEST(value, candidate)</c> in one statement; a lower or zero candidate changes nothing.</summary>
    public static async Task RaiseAsync(VybeDbContext db, Guid playerId, string stat, long candidate, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (candidate <= 0)
        {
            return;
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.player_stats (player_id, stat, value, updated_at) VALUES ({playerId}, {stat}, {candidate}, {now})
            ON CONFLICT (player_id, stat) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
                WHERE excluded.value > player_stats.value
            """, cancellationToken);
    }

    /// <summary>The number of games with at least one counted run.</summary>
    private static async Task<long> GamesPlayedAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken) =>
        await db.PlayerStats.CountAsync(s => s.PlayerId == playerId && s.Stat.StartsWith("runs:") && s.Value > 0, cancellationToken);

    /// <summary>Consecutive UTC days with a reward-eligible run (<c>runs_rewarded &gt; 0</c>) that end on <paramref name="day"/>.</summary>
    private static async Task<int> StreakEndingAsync(VybeDbContext db, Guid playerId, DateOnly day, CancellationToken cancellationToken)
    {
        var from = day.AddDays(-(AchievementStats.StreakLookbackDays - 1));
        var days = await db.PlayerDailyCounters.AsNoTracking()
            .Where(c => c.PlayerId == playerId && c.RunsRewarded > 0 && c.Day >= from && c.Day <= day)
            .Select(c => c.Day)
            .ToListAsync(cancellationToken);
        var set = days.ToHashSet();
        var streak = 0;
        while (set.Contains(day.AddDays(-streak)))
        {
            streak++;
        }

        return streak;
    }

    /// <summary>Weekly rank achievements look at the top 10 only; the rank count stops there.</summary>
    public const int WeeklyRankLimit = 10;

    /// <summary>
    /// The player's rank on the global weekly board (competition rank): 1 + the listed players with a higher best score,
    /// counted only up to <see cref="WeeklyRankLimit"/> so the verification transaction does O(10) work: any value above
    /// the limit means "outside the top 10". Same listing rule as <c>GET /leaderboards/{gameId}/weekly</c>: no private,
    /// deleting, deleted or suspended players. Reads other players' rows without a lock.
    /// </summary>
    public static async Task<int> WeeklyRankAsync(
        VybeDbContext db, Guid playerId, string gameId, DateOnly weekStart, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var scores = db.WeeklyLeaderboardScores.AsNoTracking().Where(s => s.GameId == gameId && s.WeekStart == weekStart);
        var mine = await scores.Where(s => s.PlayerId == playerId).Select(s => (int?)s.BestScore).SingleOrDefaultAsync(cancellationToken);
        if (mine is not { } best)
        {
            return int.MaxValue;
        }

        var listed = db.Players.AsNoTracking().Where(p => p.ProfileVisibility == ProfileVisibility.Players
            && p.Status != PlayerStatus.Deleting && p.Status != PlayerStatus.Deleted
            && !(p.Status == PlayerStatus.Suspended && (p.SuspendedUntil == null || p.SuspendedUntil > now)));
        var better = await scores.Where(s => s.PlayerId != playerId && s.BestScore > best)
            .Join(listed, s => s.PlayerId, p => p.Id, (s, _) => s.PlayerId)
            .Take(WeeklyRankLimit)
            .CountAsync(cancellationToken);
        return better + 1;
    }

    private static List<AchievementUnlocked> ToUnlocked(AchievementCatalog catalog, IEnumerable<UnlockRow> rows) =>
        rows.Select(r => catalog.Find(r.Code) is { } a
                ? new AchievementUnlocked(a.Code, a.Name, a.Tier, r.RewardXp)
                : new AchievementUnlocked(r.Code, r.Code, AchievementTier.Bronze, r.RewardXp))
            .OrderBy(u => catalog.Order(u.Code))
            .ToList();

    // Mapped by the snake_case naming convention: code, reward_xp.
    private sealed record UnlockRow(string Code, int RewardXp);
}

/// <summary>
/// Outbox job <c>achievements-sync</c>: the count stats and the unlock pass for one player, in its own transaction with
/// the player row lock (outbox row, then player row: the existing outbox order). Queued by friend accept and purchase,
/// which must not take a player row lock themselves (docs/design/p1-achievements.md, Locks).
/// </summary>
public static class AchievementSyncJob
{
    public const string Kind = "achievements-sync";

    public sealed record Payload(Guid PlayerId);

    public static async Task QueueAsync(VybeDbContext db, Guid playerId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var id = Guid.CreateVersion7();
        var key = $"{Kind}:{playerId}:{id}";
        var payload = JsonSerializer.Serialize(new { playerId });
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.outbox_jobs (id, kind, job_key, payload, status, attempts, run_after)
            VALUES ({id}, {Kind}, {key}, {payload}::jsonb, 'pending', 0, {now})
            """, cancellationToken);
    }

    /// <summary>Returns null when done, or the time to run again (a suspended player waits, as in the challenge job).</summary>
    public static async Task<DateTime?> RunAsync(
        VybeDbContext db, AchievementCatalog catalog, Payload payload, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payload);
        var player = await db.Players
            .FromSql($"SELECT * FROM vybe.players WHERE id = {payload.PlayerId} FOR NO KEY UPDATE")
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        if (player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted)
        {
            return null;
        }

        if (CurrentPlayer.IsSuspended(player, now))
        {
            return player.SuspendedUntil ?? now + Jobs.ChallengeResolvedJob.IndefiniteSuspensionRetry;
        }

        await AchievementProgress.SyncCountsAsync(db, catalog, player.Id, now, cancellationToken);
        return null;
    }
}
