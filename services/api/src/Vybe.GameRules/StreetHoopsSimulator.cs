namespace Vybe.GameRules;

/// <summary>Deterministic replay and scoring of one Street Hoops run (spec v1.3 §7).</summary>
public static class StreetHoopsSimulator
{
    private const long UInt32Max = uint.MaxValue;

    public static void ValidateConfig(StreetHoopsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.StreamId is < 0 or > UInt32Max)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "StreamId must be uint32");
        }

        if (config.SegmentMs <= 0 || config.DurationMs <= 0 || config.DurationMs % config.SegmentMs != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "DurationMs must be a positive multiple of SegmentMs");
        }

        if (config.XMin > config.XMax || config.PMin > config.PMax)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "waypoint ranges are empty");
        }

        if (config.MinGapMs <= 0 || config.MaxEvents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "MinGapMs must be positive and MaxEvents non-negative");
        }
    }

    public static int WaypointCount(StreetHoopsConfig config) => config.DurationMs / config.SegmentMs + 1;

    /// <summary>Draws the basket waypoints interleaved: x_0, p_0, x_1, p_1, … (§7).</summary>
    public static IReadOnlyList<Waypoint> GenerateWaypoints(StreetHoopsConfig config, long seed)
    {
        ValidateConfig(config);
        if (seed is < 0 or > UInt32Max)
        {
            throw new ArgumentOutOfRangeException(nameof(seed), "seed must be uint32");
        }

        var rng = new Pcg32((ulong)seed, (ulong)config.StreamId);
        var waypoints = new Waypoint[WaypointCount(config)];
        for (var k = 0; k < waypoints.Length; k++)
        {
            var x = rng.NextInRange(config.XMin, config.XMax);
            var p = rng.NextInRange(config.PMin, config.PMax);
            waypoints[k] = new Waypoint(x, p);
        }

        return waypoints;
    }

    /// <summary>Target position and ideal power at time t in [0, DurationMs - 1].</summary>
    public static (int TargetX, int IdealPower) TargetAt(
        StreetHoopsConfig config,
        IReadOnlyList<Waypoint> waypoints,
        int tMs)
    {
        if (tMs < 0 || tMs >= config.DurationMs)
        {
            throw new ArgumentOutOfRangeException(nameof(tMs), $"tMs {tMs} outside [0, {config.DurationMs - 1}]");
        }

        var k = tMs / config.SegmentMs;
        var f = tMs % config.SegmentMs;
        var a = waypoints[k];
        var b = waypoints[k + 1];
        return (Interpolate(a.X, b.X, f, config.SegmentMs), Interpolate(a.P, b.P, f, config.SegmentMs));
    }

    public static int StreakBonus(StreetHoopsConfig config, int streak) =>
        streak < config.StreakStart
            ? 0
            : Math.Min(config.StreakMax, config.StreakStep * (streak - config.StreakStart + 1));

    public static EvaluateResult Evaluate(EvaluateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var config = input.Config;
        ValidateConfig(config);

        var failure = FirstFailure(input);
        if (failure is not null)
        {
            return failure;
        }

        var waypoints = GenerateWaypoints(config, input.Seed);
        var shots = new List<ShotResult>(input.Submission.Events.Count);
        var score = 0;
        var streak = 0;
        var maxStreak = 0;

        foreach (var e in input.Submission.Events)
        {
            var (targetX, idealPower) = TargetAt(config, waypoints, e.TMs);
            var dx = Math.Abs(e.AimX - targetX);
            var dp = Math.Abs(e.Power - idealPower);

            ShotResultKind result;
            int points;
            if (dx <= config.PerfectDx && dp <= config.PerfectDp)
            {
                streak++;
                result = ShotResultKind.Perfect;
                points = config.PerfectPoints + StreakBonus(config, streak);
            }
            else if (dx <= config.HitDx && dp <= config.HitDp)
            {
                streak++;
                result = ShotResultKind.Hit;
                points = config.HitPoints + StreakBonus(config, streak);
            }
            else
            {
                streak = 0;
                result = ShotResultKind.Miss;
                points = 0;
            }

            maxStreak = Math.Max(maxStreak, streak);
            score += points;
            shots.Add(new ShotResult(e.TMs, targetX, idealPower, dx, dp, result, points, streak));
        }

        return EvaluateResult.Verified(score, maxStreak, shots);
    }

    private static int Interpolate(int from, int to, int f, int segmentMs) =>
        // C# integer division rounds toward zero, as §7 requires.
        from + (int)((long)(to - from) * f / segmentMs);

    private static bool IsOutOfRange(StreetHoopsConfig config, ShotEvent e) =>
        e.TMs < 0 || e.TMs > config.DurationMs - 1
        || e.AimX < InputUnits.AimXMin || e.AimX > InputUnits.AimXMax
        || e.Power < InputUnits.PowerMin || e.Power > InputUnits.PowerMax;

    /// <summary>Returns the first failing check of steps 1–9 in §7, or null if the run is valid.</summary>
    private static EvaluateResult? FirstFailure(EvaluateInput input)
    {
        var config = input.Config;
        var session = input.Session;
        var submission = input.Submission;
        var events = submission.Events;

        if (session.Status == SessionStatus.Created)
        {
            return EvaluateResult.Rejected(RejectionReason.NotStarted);
        }

        if (session.Status == SessionStatus.Expired || submission.SubmittedAtMs > session.SubmitDeadlineMs)
        {
            return EvaluateResult.Expired();
        }

        if (!string.Equals(submission.RulesetId, input.RulesetId, StringComparison.Ordinal))
        {
            return EvaluateResult.Rejected(RejectionReason.RulesetMismatch);
        }

        // Step 4 (invalid_event) runs in the API on raw JSON tokens.
        if (events.Any(e => IsOutOfRange(config, e)))
        {
            return EvaluateResult.Rejected(RejectionReason.OutOfRange);
        }

        if (events.Count > config.MaxEvents)
        {
            return EvaluateResult.Rejected(RejectionReason.TooManyEvents);
        }

        for (var i = 1; i < events.Count; i++)
        {
            if (events[i].TMs <= events[i - 1].TMs)
            {
                return EvaluateResult.Rejected(RejectionReason.NonMonotonic);
            }
        }

        for (var i = 1; i < events.Count; i++)
        {
            if (events[i].TMs - events[i - 1].TMs < config.MinGapMs)
            {
                return EvaluateResult.Rejected(RejectionReason.CadenceViolation);
            }
        }

        long maxT = events.Count == 0 ? 0 : events[^1].TMs;
        if (submission.SubmittedAtMs < session.StartedAtMs + maxT - config.EarlyToleranceMs)
        {
            return EvaluateResult.Rejected(RejectionReason.SubmittedTooEarly);
        }

        return null;
    }
}
