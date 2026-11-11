namespace Vybe.GameRules;

/// <summary>
/// The ruleset values that the API reward and session code needs for every game (spec v1.4 §7,
/// decision 4). Each game's config implements it.
/// </summary>
public interface IRewardConfig
{
    /// <summary>Game time in milliseconds.</summary>
    int DurationMs { get; }

    /// <summary>Upload allowance after <see cref="DurationMs"/>.</summary>
    int GraceMs { get; }

    /// <summary>A verified run needs at least this many input events to be reward-eligible.</summary>
    int MinEventsForRewards { get; }

    /// <summary>A verified run needs <c>submittedAt − startedAt ≥</c> this value to be reward-eligible.</summary>
    int MinRunMsForRewards { get; }

    /// <summary>Base coins need <c>score ≥</c> this value.</summary>
    int MinScoreForCoins { get; }
}
