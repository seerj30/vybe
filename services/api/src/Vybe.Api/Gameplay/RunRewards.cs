namespace Vybe.Api.Gameplay;

/// <summary>Reward formulas for a verified run (spec §7 rewards, §8 XP and levels). Tuning defaults.</summary>
internal static class RunRewards
{
    public const int LevelCap = 50;
    public const long LevelUpCoins = 100;
    public const int DailyGameXpCap = 800;

    public static long BaseCoins(int score) => Math.Min(150, 20 + (long)(score / 500) * 10);

    public static int BaseXp(int score) => Math.Min(80, 10 + (score / 400) * 5);

    /// <summary>Run number <paramref name="runNumber"/> of the reward day: full, a quarter, or nothing.</summary>
    public static long CoinsForRun(long baseCoins, int runNumber) => runNumber switch
    {
        <= 10 => baseCoins,
        <= 20 => baseCoins / 4,
        _ => 0,
    };

    /// <summary><c>min(xp, 800 - xpFromGamesToday)</c>, never negative.</summary>
    public static int XpGrant(int xp, int xpFromGamesToday) => Math.Clamp(DailyGameXpCap - xpFromGamesToday, 0, xp);

    /// <summary>Level <c>L</c> needs <c>100 * (L - 1)^2</c> total XP; capped at 50.</summary>
    public static int LevelFor(long xpTotal)
    {
        var level = 1;
        while (level < LevelCap && 100L * level * level <= xpTotal)
        {
            level++;
        }

        return level;
    }
}
