using Vybe.Api.Features;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>Spec §7 and §8 reward formulas, checked by hand.</summary>
public sealed class RewardsTests
{
    [Theory]
    [InlineData(0, 0, 10)]     // score floor: no base coins below 500 (decision 4 option D)
    [InlineData(499, 0, 15)]   // xp: 10 + floor(499/400)*5
    [InlineData(500, 30, 15)]
    [InlineData(1_950, 50, 30)]
    [InlineData(6_500, 150, 80)]
    [InlineData(22_900, 150, 80)]
    public void BaseRewards_FollowTheFormulaAndCaps(int score, long coins, int xp)
    {
        Assert.Equal(coins, Rewards.BaseCoins(score, 500));
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

    private static readonly DateTime Start = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private static StreetHoopsConfig Config() => new()
    {
        StreamId = 54,
        DurationMs = 60000,
        GraceMs = 5000,
        EarlyToleranceMs = 2000,
        SegmentMs = 4000,
        XMin = -700,
        XMax = 700,
        PMin = 300,
        PMax = 900,
        HitDx = 160,
        HitDp = 120,
        PerfectDx = 50,
        PerfectDp = 50,
        HitPoints = 100,
        PerfectPoints = 150,
        StreakStart = 3,
        StreakStep = 25,
        StreakMax = 100,
        MinGapMs = 650,
        MaxEvents = 93,
        MinShotsForRewards = 5,
        MinRunMsForRewards = 58000,
        MinScoreForCoins = 500,
    };

    [Theory]
    [InlineData(4, 60_000, NoCoinsReason.TooFewShots)]
    [InlineData(4, 1_000, NoCoinsReason.TooFewShots)]   // shots are checked first
    [InlineData(5, 57_999, NoCoinsReason.RunTooShort)]
    [InlineData(5, 58_000, null)]                        // boundary: submittedAt − startedAt = minRunMsForRewards
    [InlineData(93, 60_500, null)]
    public void Ineligibility_NeedsShotsAndAFullLengthRun(int shots, int runMs, NoCoinsReason? expected) =>
        Assert.Equal(expected, Rewards.Ineligibility(Config(), shots, Start, Start.AddMilliseconds(runMs)));

    [Theory]
    [InlineData(150, 6_500, 93, 60_000, null)]
    [InlineData(0, 6_500, 3, 60_000, NoCoinsReason.TooFewShots)]
    [InlineData(0, 6_500, 10, 3_000, NoCoinsReason.RunTooShort)]
    [InlineData(0, 499, 10, 60_000, NoCoinsReason.ScoreBelowMinimum)]
    [InlineData(0, 500, 10, 60_000, NoCoinsReason.DailyLimit)]
    public void NoCoins_NamesTheFirstReason(long coins, int score, int shots, int runMs, NoCoinsReason? expected) =>
        Assert.Equal(expected, Rewards.NoCoins(Config(), coins, score, shots, Start, Start.AddMilliseconds(runMs)));
}
