namespace Vybe.Data.Entities;

// Spec v1.3 §4: rulesets, game sessions, input traces, challenges.

public sealed class GameRuleset
{
    public required string Id { get; set; }
    public required string GameId { get; set; }
    public int Version { get; set; }

    /// <summary>Raw JSON; the checksum is computed from the canonical form in application code.</summary>
    public required string Config { get; set; }

    public required string Checksum { get; set; }
    public DateTime ActiveFrom { get; set; }
    public DateTime? RetiredAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class GameSession
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public required string GameId { get; set; }
    public required string RulesetId { get; set; }
    public long Seed { get; set; }
    public GameSessionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime StartDeadline { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? SubmitDeadline { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateOnly? RewardDay { get; set; }
    public int? EventCount { get; set; }
    public int? Score { get; set; }
    public long RewardCoins { get; set; }
    public int RewardXp { get; set; }
    public string? InputTraceHash { get; set; }
    public SessionRejectionReason? RejectionReason { get; set; }
    public string[] ReviewFlags { get; set; } = [];
    public Guid? ChallengeId { get; set; }
    public long Version { get; set; }
}

public sealed class GameInputTrace
{
    public Guid SessionId { get; set; }
    public required byte[] CompressedTrace { get; set; }
    public required string Encoding { get; set; }
    public int EventCount { get; set; }
    public DateTime ReceivedAt { get; set; }
}

public sealed class Challenge
{
    public Guid Id { get; set; }
    public Guid CreatorPlayerId { get; set; }
    public Guid CreatorSessionId { get; set; }
    public Guid? RecipientPlayerId { get; set; }
    public required byte[] InviteTokenHash { get; set; }
    public required string RulesetId { get; set; }
    public ChallengeStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime AcceptDeadline { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? CompleteDeadline { get; set; }
    public Guid? RecipientSessionId { get; set; }
    public Guid? WinnerPlayerId { get; set; }
    public Guid? RematchOfChallengeId { get; set; }
    public bool Suppressed { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public long Version { get; set; }
}
