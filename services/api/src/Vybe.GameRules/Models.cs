namespace Vybe.GameRules;

/// <summary>Street Hoops ruleset config (spec v1.3 §7). All values are integers.</summary>
public sealed record StreetHoopsConfig : IRewardConfig
{
    public required long StreamId { get; init; }
    public required int DurationMs { get; init; }
    public required int GraceMs { get; init; }
    public required int EarlyToleranceMs { get; init; }
    public required int SegmentMs { get; init; }
    public required int XMin { get; init; }
    public required int XMax { get; init; }
    public required int PMin { get; init; }
    public required int PMax { get; init; }
    public required int HitDx { get; init; }
    public required int HitDp { get; init; }
    public required int PerfectDx { get; init; }
    public required int PerfectDp { get; init; }
    public required int HitPoints { get; init; }
    public required int PerfectPoints { get; init; }
    public required int StreakStart { get; init; }
    public required int StreakStep { get; init; }
    public required int StreakMax { get; init; }
    public required int MinGapMs { get; init; }
    public required int MaxEvents { get; init; }
    public required int MinShotsForRewards { get; init; }

    /// <summary>Reward eligibility also needs submittedAt − startedAt ≥ this value (API check, not a simulator check).</summary>
    public required int MinRunMsForRewards { get; init; }

    /// <summary>Base coins need score ≥ this value (API reward rule).</summary>
    public required int MinScoreForCoins { get; init; }

    /// <summary>Street Hoops counts shots: the event floor is <see cref="MinShotsForRewards"/>.</summary>
    int IRewardConfig.MinEventsForRewards => MinShotsForRewards;
}

/// <summary>Fixed input units (§7).</summary>
public static class InputUnits
{
    public const int AimXMin = -1000;
    public const int AimXMax = 1000;
    public const int PowerMin = 0;
    public const int PowerMax = 1000;
}

public sealed record ShotEvent(int TMs, int AimX, int Power);

public sealed record Waypoint(int X, int P);

public enum SessionStatus
{
    Created,
    Active,
    Expired,
}

public sealed record SessionTiming(SessionStatus Status, long StartedAtMs, long SubmitDeadlineMs);

public sealed record Submission(string RulesetId, long SubmittedAtMs, IReadOnlyList<ShotEvent> Events);

public sealed record EvaluateInput(
    StreetHoopsConfig Config,
    string RulesetId,
    long Seed,
    SessionTiming Session,
    Submission Submission);

public enum Outcome
{
    Expired,
    Rejected,
    Verified,
}

public enum RejectionReason
{
    NotStarted,
    RulesetMismatch,
    OutOfRange,
    TooManyEvents,
    NonMonotonic,
    CadenceViolation,
    SubmittedTooEarly,
}

public enum ShotResultKind
{
    Perfect,
    Hit,
    Miss,
}

public sealed record ShotResult(
    int TMs,
    int TargetX,
    int IdealPower,
    int Dx,
    int Dp,
    ShotResultKind Result,
    int Points,
    int Streak);

/// <summary>
/// One of: expired; rejected with a reason; verified with score and shots.
/// Fields that do not apply to the outcome are null.
/// </summary>
public sealed record EvaluateResult
{
    public required Outcome Outcome { get; init; }
    public RejectionReason? Reason { get; init; }
    public int? Score { get; init; }
    public int? EventCount { get; init; }
    public int? MaxStreak { get; init; }
    public IReadOnlyList<ShotResult>? Shots { get; init; }

    public static EvaluateResult Expired() => new() { Outcome = Outcome.Expired };

    public static EvaluateResult Rejected(RejectionReason reason) =>
        new() { Outcome = Outcome.Rejected, Reason = reason };

    public static EvaluateResult Verified(int score, int maxStreak, IReadOnlyList<ShotResult> shots) =>
        new()
        {
            Outcome = Outcome.Verified,
            Score = score,
            EventCount = shots.Count,
            MaxStreak = maxStreak,
            Shots = shots,
        };
}
