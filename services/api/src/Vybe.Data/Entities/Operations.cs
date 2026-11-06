namespace Vybe.Data.Entities;

// Spec v1.3 §4: audit log, privacy requests, outbox, idempotency.

public sealed class AdminAuditLog
{
    public Guid Id { get; set; }
    public required string ActorSubject { get; set; }
    public AdminRole ActorRole { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class DataExportRequest
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public ExportStatus Status { get; set; }
    public string? StorageKey { get; set; }
    public DateTime? DownloadExpiresAt { get; set; }
    public string? FailureCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>No foreign key to players: the row outlives the player (spec §4).</summary>
public sealed class DeletionRequest
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public DeletionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class OutboxJob
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public required string JobKey { get; set; }
    public required string Payload { get; set; }
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime RunAfter { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class IdempotencyRecord
{
    public required string ActorSubject { get; set; }
    public required string Operation { get; set; }
    public Guid Key { get; set; }
    public required string RequestHash { get; set; }
    public IdempotencyStatus Status { get; set; }
    public DateTime? LockedUntil { get; set; }
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
