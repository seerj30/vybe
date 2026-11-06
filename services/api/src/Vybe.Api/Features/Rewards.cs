namespace Vybe.Api.Features;

/// <summary>Reward and level rules of spec §7 and §8 (tuning defaults, provisional per §17).</summary>
public static class Rewards
{
    public const int FullCoinRuns = 10;
    public const int QuarterCoinRuns = 20;
    public const int DailyGameXpCap = 800;
    public const int MaxLevel = 50;
    public const long LevelUpCoins = 100;

    public static long BaseCoins(int score) => Math.Min(150, 20 + (score / 500 * 10));

    public static int BaseXp(int score) => Math.Min(80, 10 + (score / 400 * 5));

    /// <summary>Coins for the run with daily number <paramref name="runNumber"/> (1-based).</summary>
    public static long CoinsForRun(long baseCoins, int runNumber) => runNumber switch
    {
        <= FullCoinRuns => baseCoins,
        <= QuarterCoinRuns => baseCoins / 4,
        _ => 0,
    };

    /// <summary>XP granted so the day's gameplay XP fills exactly to the cap.</summary>
    public static int XpGrant(int baseXp, int xpFromGamesToday) =>
        Math.Max(0, Math.Min(baseXp, DailyGameXpCap - xpFromGamesToday));

    /// <summary>Total XP needed to reach level L is 100·(L−1)²; the level is capped at 50.</summary>
    public static int LevelFor(long xpTotal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(xpTotal);
        var level = 1;
        while (level < MaxLevel && 100L * level * level <= xpTotal)
        {
            level++;
        }

        return level;
    }
}
