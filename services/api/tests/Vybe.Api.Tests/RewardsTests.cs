using Vybe.Api.Features;

namespace Vybe.Api.Tests;

/// <summary>Spec §7 and §8 reward formulas, checked by hand.</summary>
public sealed class RewardsTests
{
    [Theory]
    [InlineData(0, 20, 10)]
    [InlineData(499, 20, 15)]  // xp: 10 + floor(499/400)*5
    [InlineData(500, 30, 15)]
    [InlineData(1_950, 50, 30)]
    [InlineData(6_500, 150, 80)]
    [InlineData(22_900, 150, 80)]
    public void BaseRewards_FollowTheFormulaAndCaps(int score, long coins, int xp)
    {
        Assert.Equal(coins, Rewards.BaseCoins(score));
        Assert.Equal(xp, Rewards.BaseXp(score));
    }

    [Theory]
    [InlineData(1, 150)]
    [InlineData(10, 150)]
    [InlineData(11, 37)]
    [InlineData(20, 37)]
    [InlineData(21, 0)]
    public void CoinsForRun_UsesDailyTiers(int runNumber, long expected) =>
        Assert.Equal(expected, Rewards.CoinsForRun(150, runNumber));

    [Theory]
    [InlineData(80, 0, 80)]
    [InlineData(80, 790, 10)]
    [InlineData(80, 800, 0)]
    public void XpGrant_FillsExactlyToTheDailyCap(int baseXp, int today, int expected) =>
        Assert.Equal(expected, Rewards.XpGrant(baseXp, today));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    [InlineData(399, 2)]
    [InlineData(400, 3)]
    [InlineData(240_099, 49)]
    [InlineData(240_100, 50)]
    [InlineData(10_000_000, 50)]
    public void LevelFor_UsesSquaredThresholds_AndCapsAt50(long xp, int level) =>
        Assert.Equal(level, Rewards.LevelFor(xp));
}
