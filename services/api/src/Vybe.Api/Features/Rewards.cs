using Vybe.GameRules;

namespace Vybe.Api.Features;

/// <summary>Why a verified run earned 0 coins (spec v1.4 §7). Null when it earned coins.</summary>
public enum NoCoinsReason
{
    /// <summary>Fewer than <c>minShotsForRewards</c> shots: the run is not reward-eligible.</summary>
    TooFewShots,

    /// <summary><c>submittedAt − startedAt</c> below <c>minRunMsForRewards</c>: the run is not reward-eligible.</summary>
    RunTooShort,

    /// <summary>Score below <c>minScoreForCoins</c>: XP and quests count, base coins are 0.</summary>
    ScoreBelowMinimum,

    /// <summary>Run 21 or later of the UTC day.</summary>
    DailyLimit,
}

/// <summary>Reward and level rules of spec §7 and §8 (signed decisions 4 and 5, spec v1.4).</summary>
public static class Rewards
{
    public const int FullCoinRuns = 10;
    public const int QuarterCoinRuns = 20;
    public const int DailyGameXpCap = 800;
    public const int MaxLevel = 50;
    public const long LevelUpCoins = 100;

    /// <summary>Base coins; 0 below the ruleset's <c>minScoreForCoins</c> (decision 4 option D).</summary>
    public static long BaseCoins(int score, int minScoreForCoins) =>
        score < minScoreForCoins ? 0 : Math.Min(150, 20 + (score / 500 * 10));

    public static int BaseXp(int score) => Math.Min(80, 10 + (score / 400 * 5));

    /// <summary>
    /// Minimum play (spec §7): a verified run is reward-eligible only with at least <c>minShotsForRewards</c> shots
    /// and <c>submittedAt − startedAt ≥ minRunMsForRewards</c> on the server clock. Null means eligible.
    /// The same rule decides quest progress, <c>runs_rewarded</c> and challenge creation.
    /// </summary>
    public static NoCoinsReason? Ineligibility(StreetHoopsConfig config, int eventCount, DateTime startedAt, DateTime submittedAt)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (eventCount < config.MinShotsForRewards)
        {
            return NoCoinsReason.TooFewShots;
        }

        return (submittedAt - startedAt).TotalMilliseconds < config.MinRunMsForRewards ? NoCoinsReason.RunTooShort : null;
    }

    /// <summary>Why a verified run earned no coins, derived from the stored session; null when it earned coins.</summary>
    public static NoCoinsReason? NoCoins(
        StreetHoopsConfig config, long coins, int score, int eventCount, DateTime startedAt, DateTime submittedAt)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (coins > 0)
        {
            return null;
        }

        return Ineligibility(config, eventCount, startedAt, submittedAt)
            ?? (score < config.MinScoreForCoins ? NoCoinsReason.ScoreBelowMinimum : NoCoinsReason.DailyLimit);
    }

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
