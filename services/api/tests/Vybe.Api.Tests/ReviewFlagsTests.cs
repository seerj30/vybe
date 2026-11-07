using Vybe.Api.Features;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>Decision 4 starting thresholds for the per-run review flags, checked by hand.</summary>
public sealed class ReviewFlagsTests
{
    private static ShotResult Shot(int tMs, int dx, int dp, ShotResultKind result = ShotResultKind.Hit) =>
        new(tMs, 0, 500, dx, dp, result, 100, 1);

    /// <summary>Human-like run: uneven gaps and aim errors, few perfects.</summary>
    private static List<ShotResult> Varied(int count, Func<int, ShotResultKind>? kind = null) =>
        Enumerable.Range(0, count)
            .Select(i => Shot(i * 700 + (i % 3 * 40), dx: i % 4 * 30, dp: i % 5 * 20, (kind ?? (_ => ShotResultKind.Hit))(i)))
            .ToList();

    [Fact]
    public void ForRun_HumanLikeRun_HasNoFlags() => Assert.Empty(ReviewFlags.ForRun(Varied(40)));

    [Fact]
    public void ForRun_EmptyAndShortRuns_HaveNoFlags()
    {
        Assert.Empty(ReviewFlags.ForRun([]));
        Assert.Empty(ReviewFlags.ForRun(Enumerable.Range(0, 19).Select(i => Shot(i * 650, 0, 0, ShotResultKind.Perfect)).ToList()));
    }

    [Theory]
    [InlineData(30, 19, true)]   // 19/30 = 0.633 > 0.60
    [InlineData(30, 18, false)]  // 18/30 = 0.60 is not above the threshold
    [InlineData(29, 29, false)]  // fewer than 30 shots
    public void ForRun_PerfectRateHigh(int shots, int perfects, bool flagged) =>
        Assert.Equal(flagged, ReviewFlags.ForRun(Varied(shots, i => i < perfects ? ShotResultKind.Perfect : ShotResultKind.Hit))
            .Contains(ReviewFlags.PerfectRateHigh));

    [Theory]
    [InlineData(20, true)]   // gaps alternate 670/630 ms: std dev 20 ms < 25
    [InlineData(40, false)]  // gaps alternate 690/610 ms: std dev 40 ms
    public void ForRun_EvenGaps_SetGapVarianceLow(int jitterMs, bool flagged)
    {
        var shots = Enumerable.Range(0, 20).Select(i => Shot(i * 650 + (i % 2 * jitterMs), dx: i % 4 * 30, dp: i % 5 * 20)).ToList();
        Assert.Equal(flagged ? [ReviewFlags.GapVarianceLow] : [], ReviewFlags.ForRun(shots));
    }

    [Fact]
    public void ForRun_ExactAim_SetsAimVarianceLow_OnlyWhenBothAxesAreSteady()
    {
        var steady = Enumerable.Range(0, 20).Select(i => Shot(i * 700 + (i % 3 * 40), dx: 5, dp: 5)).ToList();
        Assert.Equal([ReviewFlags.AimVarianceLow], ReviewFlags.ForRun(steady));

        var steadyAimOnly = Enumerable.Range(0, 20).Select(i => Shot(i * 700 + (i % 3 * 40), dx: 5, dp: i % 5 * 20)).ToList();
        Assert.Empty(ReviewFlags.ForRun(steadyAimOnly));
    }

    [Fact]
    public void ForRun_PerfectBot_SetsAllRunFlags() =>
        Assert.Equal(
            [ReviewFlags.PerfectRateHigh, ReviewFlags.GapVarianceLow, ReviewFlags.AimVarianceLow],
            ReviewFlags.ForRun(Enumerable.Range(0, 93).Select(i => Shot(i * 650, 0, 0, ShotResultKind.Perfect)).ToList()));
}
