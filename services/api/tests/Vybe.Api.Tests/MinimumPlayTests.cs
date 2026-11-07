using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;

namespace Vybe.Api.Tests;

/// <summary>
/// Spec v1.4 §7 (decision 4 option D): reward eligibility needs a full-length run, base coins need a score floor,
/// and the verification transaction writes <c>review_flags</c>. Every response is checked against the contract.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class MinimumPlayTests(ApiFactory api)
{
    [Fact]
    public async Task ShortRun_IsVerifiedWithoutRewards_QuestsOrChallenge()
    {
        QuestTests.StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        await QuestTests.ListAsync(player);

        // 5 perfect shots (900 points) submitted 3 s after start: legal trace, but not a full-length run.
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.MinRunMsForRewards - 55_000));
        var run = await SubmitAsync(player, sessionId, rulesetId, Player.PerfectEvents(config, seed, 5));

        Assert.Equal("verified", run.GetProperty("status").GetString());
        Assert.Equal(900, run.GetProperty("score").GetInt32());
        AssertRewards(run.GetProperty("rewards"), coins: 0, xp: 0, "run_too_short");
        AssertRewards(await SessionRewardsAsync(player, sessionId), coins: 0, xp: 0, "run_too_short");
        Assert.Equal(0, QuestTests.Quest(await QuestTests.ListAsync(player), "play_3").GetProperty("progress").GetInt32());
        await using (var db = api.CreateDbContext())
        {
            Assert.False(await db.PlayerDailyCounters.AnyAsync(c => c.PlayerId == player.Id));
        }

        await Contract.AssertProblemAsync(await CreateChallengeAsync(player, sessionId), HttpStatusCode.UnprocessableEntity, "session_not_eligible");
    }

    [Fact]
    public async Task FullLengthRun_BelowScoreFloor_EarnsXpAndQuestProgress_ButNoCoins()
    {
        QuestTests.StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        await QuestTests.ListAsync(player);

        var (sessionId, _, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.MinRunMsForRewards));
        var misses = Enumerable.Range(0, 5)
            .Select(i => (object)new { tMs = i * config.MinGapMs, aimX = 1000, power = 0 }) // far from every basket position
            .ToArray();
        var run = await SubmitAsync(player, sessionId, rulesetId, misses);

        Assert.Equal(0, run.GetProperty("score").GetInt32());
        AssertRewards(run.GetProperty("rewards"), coins: 0, xp: 10, "score_below_minimum");
        AssertRewards(await SessionRewardsAsync(player, sessionId), coins: 0, xp: 10, "score_below_minimum");
        Assert.Equal(1, QuestTests.Quest(await QuestTests.ListAsync(player), "play_3").GetProperty("progress").GetInt32());

        // Reward-eligible, so it can still start a challenge.
        Assert.Equal(HttpStatusCode.Created, (await CreateChallengeAsync(player, sessionId)).StatusCode);
    }

    [Fact]
    public async Task EligibleRun_HasNoReason_AndAPerfectBotRunIsFlagged()
    {
        var player = await Player.CreateGuestAsync(api);
        var run = await player.PlayPerfectRunAsync();
        var sessionId = run.GetProperty("sessionId").GetGuid();

        AssertRewards(run.GetProperty("rewards"), coins: 150, xp: 80, null);
        AssertRewards(await SessionRewardsAsync(player, sessionId), coins: 150, xp: 80, null);
        Assert.Equal(
            [ReviewFlags.PerfectRateHigh, ReviewFlags.GapVarianceLow, ReviewFlags.AimVarianceLow],
            await FlagsAsync(sessionId));
    }

    [Fact]
    public async Task DailyLimit_IsTheReason_AfterRun20()
    {
        QuestTests.StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        await SetRunsRewardedAsync(player, Today(), 20);

        var run = await PlayFiveShotsAsync(player);

        AssertRewards(run.GetProperty("rewards"), coins: 0, xp: 20, "daily_limit");
    }

    [Fact]
    public async Task DailyCapStreak_FlagsRun20_OnTheSeventhFullDay()
    {
        QuestTests.StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        var today = Today();
        for (var d = 1; d <= 6; d++)
        {
            await SetRunsRewardedAsync(player, today.AddDays(-d), 20);
        }

        await SetRunsRewardedAsync(player, today, 18);

        var run19 = await PlayFiveShotsAsync(player);
        var run20 = await PlayFiveShotsAsync(player);
        var run21 = await PlayFiveShotsAsync(player);

        Assert.DoesNotContain(ReviewFlags.DailyCapStreak, await FlagsAsync(run19.GetProperty("sessionId").GetGuid()));
        Assert.Contains(ReviewFlags.DailyCapStreak, await FlagsAsync(run20.GetProperty("sessionId").GetGuid()));
        Assert.DoesNotContain(ReviewFlags.DailyCapStreak, await FlagsAsync(run21.GetProperty("sessionId").GetGuid()));
    }

    [Fact]
    public async Task DailyCapStreak_NeedsSevenConsecutiveDays()
    {
        QuestTests.StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        var today = Today();
        for (var d = 1; d <= 6; d++)
        {
            await SetRunsRewardedAsync(player, today.AddDays(-d), d == 3 ? 19 : 20); // one day below the cap
        }

        await SetRunsRewardedAsync(player, today, 19);

        var run20 = await PlayFiveShotsAsync(player);

        Assert.DoesNotContain(ReviewFlags.DailyCapStreak, await FlagsAsync(run20.GetProperty("sessionId").GetGuid()));
    }

    private DateOnly Today() => DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime);

    /// <summary>5 perfect shots (900 points: 30 base coins, 20 XP) in a full-length run.</summary>
    private async Task<JsonElement> PlayFiveShotsAsync(Player player)
    {
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs));
        return await SubmitAsync(player, sessionId, rulesetId, Player.PerfectEvents(config, seed, 5));
    }

    private static async Task<JsonElement> SubmitAsync(Player player, Guid sessionId, string rulesetId, object[] events)
    {
        var body = await Contract.AssertAsync(await player.SubmitAsync(sessionId, rulesetId, events), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("verified", body.GetProperty("status").GetString());
        return body;
    }

    private static async Task<JsonElement> SessionRewardsAsync(Player player, Guid sessionId) =>
        (await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/games/sessions/{sessionId}"), HttpStatusCode.OK, "GameSession"))
            .GetProperty("rewards");

    private static void AssertRewards(JsonElement rewards, long coins, int xp, string? reason)
    {
        Assert.Equal(coins, rewards.GetProperty("coins").GetInt64());
        Assert.Equal(xp, rewards.GetProperty("xp").GetInt32());
        Assert.Equal(reason, rewards.GetProperty("noCoinsReason").GetString());
    }

    private static async Task<HttpResponseMessage> CreateChallengeAsync(Player player, Guid sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/challenges")
        {
            Content = JsonContent.Create(new { creatorSessionId = sessionId }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await player.Http.SendAsync(request);
    }

    private async Task<string[]> FlagsAsync(Guid sessionId)
    {
        await using var db = api.CreateDbContext();
        return await db.GameSessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.ReviewFlags).SingleAsync();
    }

    private async Task SetRunsRewardedAsync(Player player, DateOnly day, int runs)
    {
        await using var db = api.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.player_daily_counters (player_id, day, runs_rewarded) VALUES ({player.Id}, {day}, {runs})
            ON CONFLICT (player_id, day) DO UPDATE SET runs_rewarded = {runs}
            """);
    }
}
