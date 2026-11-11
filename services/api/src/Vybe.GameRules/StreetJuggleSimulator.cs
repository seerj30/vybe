namespace Vybe.GameRules;

/// <summary>
/// The Street Juggle simulation (docs/design/p1-second-game.md §2.3–2.4), step by step as the TS
/// reference <c>StreetJuggleSim</c>. All arithmetic is integer; heights and speeds are <c>long</c>.
/// </summary>
public sealed class StreetJuggleSim
{
    private readonly StreetJuggleConfig _config;
    private readonly Pcg32 _rng;
    private readonly List<JuggleTouchResult> _touches = [];
    private int _nextTick;
    private bool _inPlay;
    private int _ballLane;
    private long _y;
    private long _vy;
    private bool _star;
    private int _respawnTick;
    private int _lockUntilTick;
    private int _streak;
    private int _lastTouchTMs = -1;

    public StreetJuggleSim(StreetJuggleConfig config, long seed)
    {
        StreetJuggleSimulator.ValidateConfig(config);
        if (seed is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(seed), "seed must be uint32");
        }

        _config = config;
        _rng = new Pcg32((ulong)seed, (ulong)config.StreamId);
        TotalTicks = config.DurationMs / config.TickMs;
    }

    public int TotalTicks { get; }

    public int CurrentTick => _nextTick;

    public JuggleBall Ball => new(_inPlay, _ballLane, _y, _vy, _star);

    public int Score { get; private set; }

    public int MaxStreak { get; private set; }

    public int KickCount { get; private set; }

    public int PerfectCount { get; private set; }

    public int DropCount { get; private set; }

    public IReadOnlyList<JuggleTouchResult> Touches => _touches;

    public bool IsLocked(int tick) => tick < _lockUntilTick;

    /// <summary>Runs every tick before <paramref name="tick"/> without touches (clamped to the end of the run).</summary>
    public void AdvanceTo(int tick)
    {
        var end = Math.Min(tick, TotalTicks);
        while (_nextTick < end)
        {
            RunTick(null);
        }
    }

    /// <summary>Runs the ticks up to the touch, then the touch tick with the touch. The caller checks cadence and ranges.</summary>
    public JuggleTouchResult Touch(JuggleTouch touch)
    {
        ArgumentNullException.ThrowIfNull(touch);
        if (touch.TMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(touch), "touch outside the run");
        }

        var tick = touch.TMs / _config.TickMs;
        if (tick >= TotalTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(touch), "touch outside the run");
        }

        if (tick < _nextTick || touch.TMs <= _lastTouchTMs)
        {
            throw new ArgumentOutOfRangeException(nameof(touch), $"touch at {touch.TMs} ms is before the simulation time");
        }

        AdvanceTo(tick);
        _lastTouchTMs = touch.TMs;
        return RunTick(touch)!;
    }

    private JuggleTouchResult? RunTick(JuggleTouch? touch)
    {
        var c = _config;
        var k = _nextTick;

        // 1. Serve.
        if (!_inPlay && k >= _respawnTick)
        {
            _ballLane = _rng.NextInRange(0, c.Lanes - 1);
            _star = _rng.NextInRange(0, 99) < c.StarPercent;
            _y = c.ServeY;
            _vy = 0;
            _inPlay = true;
        }

        // 2. Touch.
        var result = touch is null ? null : ApplyTouch(touch, k);

        // 3. Physics.
        if (_inPlay)
        {
            _vy -= StreetJuggleSimulator.GravityAt(c, k);
            _y += _vy;
            if (_y <= 0)
            {
                _streak = 0;
                DropCount++;
                _inPlay = false;
                _respawnTick = k + 1 + (c.RespawnMs / c.TickMs);
            }
        }

        _nextTick = k + 1;
        return result;
    }

    private JuggleTouchResult ApplyTouch(JuggleTouch touch, int k)
    {
        var c = _config;
        var ballLane = _inPlay ? _ballLane : -1;
        var ballY = _inPlay ? _y : 0;
        var dy = Math.Abs(_y - c.KickY);
        var kick = k >= _lockUntilTick && _inPlay && touch.Lane == _ballLane && _vy <= 0 && dy <= c.GoodDy;

        JuggleTouchKind result;
        var points = 0;
        if (kick)
        {
            result = dy <= c.PerfectDy ? JuggleTouchKind.Perfect : JuggleTouchKind.Good;
            _streak++;
            points = (result == JuggleTouchKind.Perfect ? c.PerfectPoints : c.GoodPoints)
                + StreetJuggleSimulator.StreakBonus(c, _streak)
                + (_star ? c.StarPoints : 0);
            KickCount++;
            if (result == JuggleTouchKind.Perfect)
            {
                PerfectCount++;
            }

            // Launch: vy, lane, star (design §2.4 draw order). The ball leaves from the touch height.
            _vy = _rng.NextInRange(c.LaunchMin, c.LaunchMax);
            _ballLane = _rng.NextInRange(0, c.Lanes - 1);
            _star = _rng.NextInRange(0, 99) < c.StarPercent;
        }
        else
        {
            result = JuggleTouchKind.Whiff;
            _streak = 0;
            if (k >= _lockUntilTick)
            {
                _lockUntilTick = k + (c.WhiffLockMs / c.TickMs);
            }
        }

        Score += points;
        MaxStreak = Math.Max(MaxStreak, _streak);
        var touchResult = new JuggleTouchResult(touch.TMs, touch.Lane, ballLane, ballY, result, points, _streak);
        _touches.Add(touchResult);
        return touchResult;
    }
}

/// <summary>Deterministic replay and scoring of one Street Juggle run (docs/design/p1-second-game.md §2–4).</summary>
public static class StreetJuggleSimulator
{
    private const int MaxDurationMs = 600_000;
    private const long MaxMagnitude = 1_000_000;

    public static void ValidateConfig(StreetJuggleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (FirstConfigProblem(config) is { } problem)
        {
            throw new ArgumentOutOfRangeException(nameof(config), problem);
        }
    }

    private static string? FirstConfigProblem(StreetJuggleConfig c)
    {
        if (c.StreamId is < 0 or > uint.MaxValue)
        {
            return "StreamId must be uint32";
        }

        if (c.TickMs <= 0)
        {
            return "TickMs must be positive";
        }

        if (c.DurationMs <= 0 || c.DurationMs > MaxDurationMs || c.DurationMs % c.TickMs != 0)
        {
            return "DurationMs must be a positive multiple of TickMs, at most 600000";
        }

        if (c.RespawnMs < 0 || c.RespawnMs % c.TickMs != 0 || c.WhiffLockMs < 0 || c.WhiffLockMs % c.TickMs != 0)
        {
            return "RespawnMs and WhiffLockMs must be non-negative multiples of TickMs";
        }

        if (c.LevelMs <= 0 || c.LevelMs % c.TickMs != 0)
        {
            return "LevelMs must be a positive multiple of TickMs";
        }

        if (c.Lanes < 1)
        {
            return "Lanes must be at least 1";
        }

        if (c.MinGapMs < c.TickMs || c.MaxEvents < 0)
        {
            return "MinGapMs must be at least TickMs and MaxEvents non-negative";
        }

        if (c.PerfectDy < 0 || c.PerfectDy > c.GoodDy)
        {
            return "PerfectDy must be in [0, GoodDy]";
        }

        if ((long)c.KickY - c.GoodDy <= 0 || c.ServeY <= (long)c.KickY + c.GoodDy || c.ServeY > MaxMagnitude)
        {
            return "heights must satisfy 0 < KickY - GoodDy and KickY + GoodDy < ServeY <= 1000000";
        }

        if (c.GravityStart <= 0 || c.GravityStep < 0
            || c.GravityStart + ((long)c.GravityStep * (c.DurationMs / c.LevelMs)) > MaxMagnitude)
        {
            return "gravity must be positive and at most 1000000";
        }

        if (c.LaunchMin <= 0 || c.LaunchMin > c.LaunchMax || c.LaunchMax > MaxMagnitude)
        {
            return "launch range must be in (0, 1000000]";
        }

        if (c.StarPercent is < 0 or > 100)
        {
            return "StarPercent must be in [0, 100]";
        }

        if (c.GoodPoints < 0 || c.PerfectPoints < 0 || c.StarPoints < 0 || c.StreakStep < 0 || c.StreakMax < 0)
        {
            return "points must be non-negative";
        }

        return c.StreakStart < 1 ? "StreakStart must be at least 1" : null;
    }

    public static int StreakBonus(StreetJuggleConfig config, int streak)
    {
        ArgumentNullException.ThrowIfNull(config);
        return streak < config.StreakStart
            ? 0
            : Math.Min(config.StreakMax, config.StreakStep * (streak - config.StreakStart + 1));
    }

    /// <summary>Gravity per tick² at tick <paramref name="tick"/> (one level per <c>LevelMs</c>).</summary>
    public static int GravityAt(StreetJuggleConfig config, int tick)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GravityStart + (config.GravityStep * (int)((long)tick * config.TickMs / config.LevelMs));
    }

    public static JuggleEvaluateResult Evaluate(JuggleEvaluateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateConfig(input.Config);

        var failure = FirstFailure(input);
        if (failure is not null)
        {
            return failure;
        }

        var sim = new StreetJuggleSim(input.Config, input.Seed);
        foreach (var touch in input.Submission.Events)
        {
            sim.Touch(touch);
        }

        sim.AdvanceTo(sim.TotalTicks);
        return new JuggleEvaluateResult
        {
            Outcome = Outcome.Verified,
            Score = sim.Score,
            EventCount = sim.Touches.Count,
            MaxStreak = sim.MaxStreak,
            KickCount = sim.KickCount,
            PerfectCount = sim.PerfectCount,
            DropCount = sim.DropCount,
            Touches = sim.Touches,
        };
    }

    private static bool IsOutOfRange(StreetJuggleConfig config, JuggleTouch e) =>
        e.TMs < 0 || e.TMs > config.DurationMs - 1 || e.Lane < 0 || e.Lane > config.Lanes - 1;

    /// <summary>Returns the first failing check of design §4 (steps 1–3, 5–9), or null if the run is valid.</summary>
    private static JuggleEvaluateResult? FirstFailure(JuggleEvaluateInput input)
    {
        var config = input.Config;
        var session = input.Session;
        var submission = input.Submission;
        var events = submission.Events;

        if (session.Status == SessionStatus.Created)
        {
            return JuggleEvaluateResult.Rejected(RejectionReason.NotStarted);
        }

        if (session.Status == SessionStatus.Expired || submission.SubmittedAtMs > session.SubmitDeadlineMs)
        {
            return JuggleEvaluateResult.Expired();
        }

        if (!string.Equals(submission.RulesetId, input.RulesetId, StringComparison.Ordinal))
        {
            return JuggleEvaluateResult.Rejected(RejectionReason.RulesetMismatch);
        }

        // Step 4 (invalid_event) runs in the API on raw JSON tokens.
        if (events.Any(e => IsOutOfRange(config, e)))
        {
            return JuggleEvaluateResult.Rejected(RejectionReason.OutOfRange);
        }

        if (events.Count > config.MaxEvents)
        {
            return JuggleEvaluateResult.Rejected(RejectionReason.TooManyEvents);
        }

        for (var i = 1; i < events.Count; i++)
        {
            if (events[i].TMs <= events[i - 1].TMs)
            {
                return JuggleEvaluateResult.Rejected(RejectionReason.NonMonotonic);
            }
        }

        for (var i = 1; i < events.Count; i++)
        {
            if (events[i].TMs - events[i - 1].TMs < config.MinGapMs)
            {
                return JuggleEvaluateResult.Rejected(RejectionReason.CadenceViolation);
            }
        }

        long maxT = events.Count == 0 ? 0 : events[^1].TMs;
        if (submission.SubmittedAtMs < session.StartedAtMs + maxT - config.EarlyToleranceMs)
        {
            return JuggleEvaluateResult.Rejected(RejectionReason.SubmittedTooEarly);
        }

        return null;
    }
}
