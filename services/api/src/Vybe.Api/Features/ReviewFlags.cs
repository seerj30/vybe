using Microsoft.EntityFrameworkCore;
using Vybe.Data;
using Vybe.GameRules;

namespace Vybe.Api.Features;

/// <summary>
/// Anti-automation signals for <c>game_sessions.review_flags</c> (spec §7, decision 4 starting thresholds).
/// Flags only: a flagged run stays verified and rewarded; the flags feed the admin review.
/// </summary>
public static class ReviewFlags
{
    public const string PerfectRateHigh = "perfect_rate_high";
    public const string GapVarianceLow = "gap_variance_low";
    public const string AimVarianceLow = "aim_variance_low";
    public const string DailyCapStreak = "daily_cap_streak";
    public const string TimingSpreadLow = "timing_spread_low";

    public const int PerfectRateMinShots = 30;
    public const double PerfectRateMax = 0.60;
    public const int VarianceMinShots = 20;
    public const double GapStdDevMinMs = 25;
    public const double AimStdDevMin = 10;
    public const int DailyCapRuns = Rewards.QuarterCoinRuns;
    public const int DailyCapStreakDays = 7;

    /// <summary>Street Juggle: a skilled human is near 75 % perfect touches (design p1-second-game §2.6, §4).</summary>
    public const double JugglePerfectRateMax = 0.90;

    /// <summary>
    /// Street Juggle: the population standard deviation of the signed touch error <c>(ballY − kickY) / goodDy</c> over
    /// the touches that kicked the ball. Simulated human timing (σ 15–60 ms, any bias) stays at 0.10 or more (5th
    /// percentile over 120 seeds); a bot with a fixed offset, or a fixed offset with up to ±10 ms jitter, stays at 0.075
    /// or less. Such a bot plays "good, not perfect" on purpose and so avoids <see cref="PerfectRateHigh"/>.
    /// </summary>
    public const double JuggleTimingSpreadMin = 0.08;

    /// <summary>Per-run flags from the canonical shots, in a stable order. Population standard deviation.</summary>
    public static List<string> ForRun(IReadOnlyList<ShotResult> shots)
    {
        ArgumentNullException.ThrowIfNull(shots);
        var flags = new List<string>();
        if (shots.Count >= PerfectRateMinShots
            && (double)shots.Count(s => s.Result == ShotResultKind.Perfect) / shots.Count > PerfectRateMax)
        {
            flags.Add(PerfectRateHigh);
        }

        if (shots.Count >= VarianceMinShots)
        {
            var gaps = shots.Skip(1).Select((s, i) => (double)(s.TMs - shots[i].TMs)).ToList();
            if (StdDev(gaps) < GapStdDevMinMs)
            {
                flags.Add(GapVarianceLow);
            }

            if (StdDev(shots.Select(s => (double)s.Dx).ToList()) < AimStdDevMin
                && StdDev(shots.Select(s => (double)s.Dp).ToList()) < AimStdDevMin)
            {
                flags.Add(AimVarianceLow);
            }
        }

        return flags;
    }

    /// <summary>
    /// Street Juggle flags, in a stable order: <see cref="PerfectRateHigh"/> and <see cref="TimingSpreadLow"/>. The seeded
    /// flights make touch gaps vary for bots too, so the Street Hoops variance flags do not apply (design p1-second-game §4).
    /// </summary>
    public static List<string> ForJuggle(IReadOnlyList<JuggleTouchResult> touches, StreetJuggleConfig config)
    {
        ArgumentNullException.ThrowIfNull(touches);
        ArgumentNullException.ThrowIfNull(config);
        var flags = new List<string>();
        if (touches.Count >= PerfectRateMinShots
            && (double)touches.Count(t => t.Result == JuggleTouchKind.Perfect) / touches.Count > JugglePerfectRateMax)
        {
            flags.Add(PerfectRateHigh);
        }

        var errors = touches.Where(t => t.Result != JuggleTouchKind.Whiff)
            .Select(t => (double)(t.BallY - config.KickY) / config.GoodDy).ToList();
        if (errors.Count >= PerfectRateMinShots && StdDev(errors) < JuggleTimingSpreadMin)
        {
            flags.Add(TimingSpreadLow);
        }

        return flags;
    }

    /// <summary>
    /// True when this run is the player's <see cref="DailyCapRuns"/>th reward-eligible run of <paramref name="day"/> and the
    /// six days before also reached that count (7 consecutive UTC days). One flagged run per player and day.
    /// </summary>
    public static async Task<bool> IsDailyCapStreakAsync(
        VybeDbContext db, Guid playerId, DateOnly day, int runNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (runNumber != DailyCapRuns)
        {
            return false;
        }

        var from = day.AddDays(-(DailyCapStreakDays - 1));
        var to = day.AddDays(-1);
        var fullDays = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM vybe.player_daily_counters
            WHERE player_id = {playerId} AND day BETWEEN {from} AND {to} AND runs_rewarded >= {DailyCapRuns}
            """).SingleAsync(cancellationToken);
        return fullDays == DailyCapStreakDays - 1;
    }

    private static double StdDev(List<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }
}
