namespace Vybe.GameRules;

/// <summary>Street Juggle ruleset config (docs/design/p1-second-game.md §2.2). All values are integers.</summary>
public sealed record StreetJuggleConfig : IRewardConfig
{
    public required long StreamId { get; init; }
    public required int DurationMs { get; init; }
    public required int GraceMs { get; init; }
    public required int EarlyToleranceMs { get; init; }
    public required int TickMs { get; init; }
    public required int Lanes { get; init; }
    public required int ServeY { get; init; }
    public required int KickY { get; init; }
    public required int GoodDy { get; init; }
    public required int PerfectDy { get; init; }
    public required int GravityStart { get; init; }
    public required int GravityStep { get; init; }
    public required int LevelMs { get; init; }
    public required int LaunchMin { get; init; }
    public required int LaunchMax { get; init; }
    public required int RespawnMs { get; init; }
    public required int WhiffLockMs { get; init; }
    public required int GoodPoints { get; init; }
    public required int PerfectPoints { get; init; }
    public required int StarPercent { get; init; }
    public required int StarPoints { get; init; }
    public required int StreakStart { get; init; }
    public required int StreakStep { get; init; }
    public required int StreakMax { get; init; }
    public required int MinGapMs { get; init; }
    public required int MaxEvents { get; init; }
    public required int MinEventsForRewards { get; init; }
    public required int MinRunMsForRewards { get; init; }
    public required int MinScoreForCoins { get; init; }
}

/// <summary>One touch of a spot: lane 0 = left, 1 = centre, 2 = right.</summary>
public sealed record JuggleTouch(int TMs, int Lane);

public enum JuggleTouchKind
{
    Perfect,
    Good,
    Whiff,
}

/// <param name="BallLane">Lane of the ball at the touch; −1 while the ball waits for its serve.</param>
/// <param name="BallY">Height of the ball at the touch; 0 while the ball waits.</param>
public sealed record JuggleTouchResult(int TMs, int Lane, int BallLane, long BallY, JuggleTouchKind Result, int Points, int Streak);

public sealed record JuggleSubmission(string RulesetId, long SubmittedAtMs, IReadOnlyList<JuggleTouch> Events);

public sealed record JuggleEvaluateInput(
    StreetJuggleConfig Config,
    string RulesetId,
    long Seed,
    SessionTiming Session,
    JuggleSubmission Submission);

/// <summary>The ball as the client draws it.</summary>
public sealed record JuggleBall(bool InPlay, int Lane, long Y, long Vy, bool Star);

/// <summary>One of: expired; rejected with a reason; verified with score and touches. Fields that do not apply are null.</summary>
public sealed record JuggleEvaluateResult
{
    public required Outcome Outcome { get; init; }
    public RejectionReason? Reason { get; init; }
    public int? Score { get; init; }
    public int? EventCount { get; init; }
    public int? MaxStreak { get; init; }
    public int? KickCount { get; init; }
    public int? PerfectCount { get; init; }
    public int? DropCount { get; init; }
    public IReadOnlyList<JuggleTouchResult>? Touches { get; init; }

    public static JuggleEvaluateResult Expired() => new() { Outcome = Outcome.Expired };

    public static JuggleEvaluateResult Rejected(RejectionReason reason) => new() { Outcome = Outcome.Rejected, Reason = reason };
}
