namespace Vybe.Data.Entities;

// Spec v1.3 §4: players, guest sessions, avatar, blocks, reports.

public sealed class Player
{
    public Guid Id { get; set; }
    public string? AuthSubject { get; set; }
    public required string Handle { get; set; }
    public required string HandleNormalized { get; set; }
    public string? DisplayName { get; set; }
    public PlayerKind Kind { get; set; }
    public PlayerStatus Status { get; set; }
    public int Level { get; set; } = 1;
    public long XpTotal { get; set; }
    public DateTime? SuspendedUntil { get; set; }
    public string? SuspensionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public sealed class GuestSession
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AvatarConfig
{
    public Guid PlayerId { get; set; }
    public required string BodyPresetId { get; set; }
    public required string SkinToneId { get; set; }
    public required string FacePresetId { get; set; }
    public required string HairPresetId { get; set; }
    public required string HairColorId { get; set; }

    /// <summary>Raw JSON in the spec §4 <c>equipped_json</c> shape.</summary>
    public required string EquippedJson { get; set; }

    public long Revision { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PlayerBlock
{
    public Guid BlockerPlayerId { get; set; }
    public Guid BlockedPlayerId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PlayerReport
{
    public Guid Id { get; set; }
    public Guid ReporterPlayerId { get; set; }
    public Guid TargetPlayerId { get; set; }
    public ReportReason Reason { get; set; }
    public string? Details { get; set; }
    public ReportStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
}
