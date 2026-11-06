using System.Text.Json;

namespace Vybe.Api.Gameplay;

// Response bodies of the gameplay operations (OpenAPI schemas of the same names).

internal sealed record RulesetResponse(string Id, string GameId, int Version, JsonElement Config, string Checksum);

internal sealed record SessionCreatedResponse(
    Guid SessionId,
    string Status,
    string RulesetId,
    string RulesetChecksum,
    DateTime StartDeadline);

internal sealed record SessionStartedResponse(long Seed, DateTime StartedAt, DateTime SubmitDeadline);

internal sealed record RewardsResponse(long Coins, int Xp, int LevelAfter);

internal sealed record GameSessionResponse(
    Guid SessionId,
    string GameId,
    string RulesetId,
    string Status,
    Guid? ChallengeId,
    DateTime CreatedAt,
    DateTime StartDeadline,
    DateTime? StartedAt,
    DateTime? SubmitDeadline,
    int? Score,
    RewardsResponse? Rewards,
    string? RejectionReason);

internal sealed record SubmitVerifiedResponse(Guid SessionId, string Status, int Score, RewardsResponse Rewards);

internal sealed record SubmitRejectedResponse(Guid SessionId, string Status, string RejectionReason);

/// <summary><c>SubmitExpired</c> and <c>SubmitAccepted</c>: only the id and the status.</summary>
internal sealed record SubmitStatusResponse(Guid SessionId, string Status);
