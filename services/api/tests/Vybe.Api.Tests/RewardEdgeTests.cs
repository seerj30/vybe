using System.Net;
using Microsoft.EntityFrameworkCore;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>Reward edge cases of spec §7 (reward day) and §8 (one level reward per new level).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RewardEdgeTests(ApiFactory api)
{
    // Five perfect shots (0 … 2600 ms): 150 + 150 + 175 + 200 + 225 = 900 points,
    // so min(150, 20 + floor(900/500)*10) = 30 coins and min(80, 10 + floor(900/400)*5) = 20 XP.
    private const long FivePerfectCoins = 30;
    private const int FivePerfectXp = 20;

    [Fact]
    public async Task RunStartedBeforeMidnightUtc_CountsOnItsStartDay()
    {
        var player = await Player.CreateGuestAsync(api);
        var now = api.Clock.GetUtcNow();
        api.Clock.Advance(now.Date.AddDays(1).AddSeconds(-10) - now.UtcDateTime);
        var startDay = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime);

        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(20)); // verification happens on the next UTC day
        var run = await Contract.AssertAsync(
            await player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, 5)), HttpStatusCode.OK, "SubmitResult");

        Assert.Equal("verified", run.GetProperty("status").GetString());
        await using var db = api.CreateDbContext();
        var counter = await db.PlayerDailyCounters.AsNoTracking().SingleAsync(c => c.PlayerId == player.Id);
        Assert.Equal(startDay, counter.Day);
        Assert.Equal(1, counter.RunsRewarded);
        Assert.Equal(startDay, (await db.GameSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId)).RewardDay);
    }

    [Fact]
    public async Task OneXpGrantCrossingTwoLevels_WritesOneLevelRewardPerLevel()
    {
        var player = await Player.CreateGuestAsync(api);
        // players.level caches the level of xp_total; 395 XP is level 2, but keeping the cache at 1 lets one
        // 20 XP grant (415 XP = level 3) cross two thresholds: level 2 at 100 XP and level 3 at 400 XP.
        await using (var db = api.CreateDbContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE vybe.players SET xp_total = 395, level = 1 WHERE id = {player.Id}");
        }

        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));
        var run = await Contract.AssertAsync(
            await player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, 5)), HttpStatusCode.OK, "SubmitResult");

        var rewards = run.GetProperty("rewards");
        Assert.Equal(FivePerfectCoins, rewards.GetProperty("coins").GetInt64());
        Assert.Equal(FivePerfectXp, rewards.GetProperty("xp").GetInt32());
        Assert.Equal(3, rewards.GetProperty("levelAfter").GetInt32());

        await using var check = api.CreateDbContext();
        var ledger = await check.WalletTransactions.AsNoTracking()
            .Where(t => t.PlayerId == player.Id)
            .OrderBy(t => t.BalanceAfter)
            .Select(t => new { t.Reason, t.LedgerKey, t.Delta, t.BalanceAfter })
            .ToListAsync();
        Assert.Equal(
            [
                new { Reason = LedgerReason.GameReward, LedgerKey = $"game:{sessionId}", Delta = FivePerfectCoins, BalanceAfter = FivePerfectCoins },
                new { Reason = LedgerReason.LevelReward, LedgerKey = "level:2", Delta = 100L, BalanceAfter = FivePerfectCoins + 100 },
                new { Reason = LedgerReason.LevelReward, LedgerKey = "level:3", Delta = 100L, BalanceAfter = FivePerfectCoins + 200 },
            ],
            ledger);
        Assert.Equal(FivePerfectCoins + 200, (await check.Wallets.AsNoTracking().SingleAsync(w => w.PlayerId == player.Id)).Balance);
        Assert.Equal(3, (await check.Players.AsNoTracking().SingleAsync(p => p.Id == player.Id)).Level);
    }
}
