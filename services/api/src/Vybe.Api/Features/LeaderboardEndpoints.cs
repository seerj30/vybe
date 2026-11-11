using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record LeaderboardEntry(int Rank, string Handle, string? DisplayName, int Level, int Score, bool IsMe);

public sealed record LeaderboardMe(int Rank, int Score, bool Listed);

public sealed record WeeklyLeaderboard(
    string GameId, string Scope, string Week, DateOnly WeekStart, DateOnly WeekEnd, IReadOnlyList<LeaderboardEntry> Entries, LeaderboardMe? Me);

/// <summary>
/// Spec §16 P1 weekly leaderboard (docs/design/p1-friends-leaderboard.md): per game, ISO week in UTC, the best counted
/// score per player from <c>weekly_leaderboard_scores</c>, which the run verification writes.
/// </summary>
public static partial class LeaderboardEndpoints
{
    public const int GlobalSize = 100;

    /// <summary>The first week the API serves (the boards start with the P1 release).</summary>
    public static readonly DateOnly FirstWeekStart = DateOnly.FromDateTime(ISOWeek.ToDateTime(2026, 1, DayOfWeek.Monday));

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/leaderboards/{gameId}/weekly", GetWeeklyAsync);
    }

    /// <summary>Monday (UTC date) of the ISO week that contains <paramref name="utc"/>.</summary>
    public static DateOnly WeekStart(DateTime utc)
    {
        var day = DateOnly.FromDateTime(utc);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    public static string WeekLabel(DateOnly weekStart)
    {
        var date = weekStart.ToDateTime(TimeOnly.MinValue);
        return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(date):D4}-W{ISOWeek.GetWeekOfYear(date):D2}");
    }

    /// <summary>
    /// Whether a verified run counts for the board: reward-eligible (spec v1.4 §7 minimum play), not a challenge recipient
    /// run (its seed is known in advance) and without review flags. A suspended player gets no verified reward at all.
    /// </summary>
    public static bool Counts(GameSession session, bool rewardEligible)
    {
        ArgumentNullException.ThrowIfNull(session);
        return rewardEligible && session.ChallengeId is null && session.ReviewFlags.Length == 0;
    }

    /// <summary>
    /// Keeps the player's best score of the run's week (by <c>started_at</c>, the clock of <c>reward_day</c>). Runs in the
    /// verification transaction and locks one row of this player only (invariant 10). A tie keeps the earlier run.
    /// </summary>
    public static async Task RecordAsync(VybeDbContext db, GameSession session, int score, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);
        var week = WeekStart(session.StartedAt!.Value);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.weekly_leaderboard_scores (game_id, week_start, player_id, best_score, session_id, achieved_at)
            VALUES ({session.GameId}, {week}, {session.PlayerId}, {score}, {session.Id}, {now})
            ON CONFLICT (game_id, week_start, player_id) DO UPDATE
                SET best_score = excluded.best_score, session_id = excluded.session_id, achieved_at = excluded.achieved_at
                WHERE excluded.best_score > weekly_leaderboard_scores.best_score
            """, cancellationToken);
    }

    [GeneratedRegex("^(\\d{4})-W(\\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex WeekPattern();

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GameIdPattern();

    private static async Task<WeeklyLeaderboard> GetWeeklyAsync(
        string gameId, string? scope, string? week, HttpContext context, VybeDbContext db, CurrentPlayer current, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        var playerId = await current.GetIdAsync(cancellationToken);
        var friends = scope switch
        {
            null or "global" => false,
            "friends" => true,
            _ => throw Problem.ValidationFailed("/scope", "invalid", "scope must be global or friends"),
        };
        var now = clock.UtcNowForDb();
        var weekStart = ParseWeek(week, WeekStart(now));
        if (!GameIdPattern().IsMatch(gameId) || !await db.GameRulesets.AnyAsync(r => r.GameId == gameId, cancellationToken))
        {
            throw Problem.Of(404, "not_found");
        }

        // Deleting, deleted and currently suspended players are never on a board.
        var players = db.Players.AsNoTracking().Where(p => p.Status != PlayerStatus.Deleting && p.Status != PlayerStatus.Deleted
            && !(p.Status == PlayerStatus.Suspended && (p.SuspendedUntil == null || p.SuspendedUntil > now)));
        var listed = friends
            // The caller and the accepted friends; a private friend is shown to friends (design doc, open question 1).
            ? players.Where(p => p.Id == playerId || FriendEndpoints.FriendIds(db, playerId).Contains(p.Id))
            // Strangers never see a private profile (spec §17 decision 6).
            : players.Where(p => p.ProfileVisibility == ProfileVisibility.Players);
        var scores = db.WeeklyLeaderboardScores.AsNoTracking().Where(s => s.GameId == gameId && s.WeekStart == weekStart);

        var rows = await scores
            .Join(listed, s => s.PlayerId, p => p.Id, (s, p) => new { s.PlayerId, s.BestScore, s.AchievedAt, p.Handle, p.DisplayName, p.Level })
            .OrderByDescending(r => r.BestScore).ThenBy(r => r.AchievedAt).ThenBy(r => r.PlayerId)
            .Take(friends ? FriendEndpoints.MaxFriends + 1 : GlobalSize)
            .ToListAsync(cancellationToken);

        var entries = new List<LeaderboardEntry>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            // Competition rank: equal scores share a rank (1, 2, 2, 4).
            var rank = i > 0 && rows[i].BestScore == rows[i - 1].BestScore ? entries[i - 1].Rank : i + 1;
            entries.Add(new LeaderboardEntry(rank, rows[i].Handle, rows[i].DisplayName, rows[i].Level, rows[i].BestScore, rows[i].PlayerId == playerId));
        }

        LeaderboardMe? me = null;
        if (entries.FirstOrDefault(e => e.IsMe) is { } mine)
        {
            me = new LeaderboardMe(mine.Rank, mine.Score, true);
        }
        else if (await scores.Where(s => s.PlayerId == playerId && players.Any(p => p.Id == playerId))
                     .Select(s => (int?)s.BestScore).SingleOrDefaultAsync(cancellationToken) is { } myScore)
        {
            // Not in the list (outside the top 100, or a private profile): the rank among listed players.
            var better = await scores.Where(s => s.BestScore > myScore && s.PlayerId != playerId)
                .Join(listed, s => s.PlayerId, p => p.Id, (s, _) => s.PlayerId)
                .CountAsync(cancellationToken);
            me = new LeaderboardMe(better + 1, myScore, false);
        }

        return new WeeklyLeaderboard(gameId, friends ? "friends" : "global", WeekLabel(weekStart), weekStart, weekStart.AddDays(6), entries, me);
    }

    /// <summary>ISO week <c>YYYY-Www</c>; the current week when absent; never before 2026-W01 or after the current week.</summary>
    private static DateOnly ParseWeek(string? week, DateOnly current)
    {
        if (week is null)
        {
            return current;
        }

        var match = WeekPattern().Match(week);
        if (match.Success)
        {
            var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var number = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (year is >= 2026 and <= 9998 && number >= 1 && number <= ISOWeek.GetWeeksInYear(year))
            {
                var start = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, number, DayOfWeek.Monday));
                if (start >= FirstWeekStart && start <= current)
                {
                    return start;
                }
            }
        }

        throw Problem.ValidationFailed("/week", "invalid", "week must be an ISO week (YYYY-Www) from 2026-W01 to the current week");
    }
}
