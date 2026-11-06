using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vybe.Data.Entities;

namespace Vybe.Data.Configuration;

internal sealed class AdminAuditLogConfiguration : IEntityTypeConfiguration<AdminAuditLog>
{
    public void Configure(EntityTypeBuilder<AdminAuditLog> b)
    {
        b.ToTable("admin_audit_logs", t =>
        {
            t.EnumCheck<AdminAuditLog, AdminRole>("actor_role");
            t.Check("reason_length", "length(reason) BETWEEN 3 AND 500");
        });
        b.Property(x => x.ActorRole).HasTextEnum();
        b.Property(x => x.BeforeJson).Jsonb();
        b.Property(x => x.AfterJson).Jsonb();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAt }).IsDescending(false, false, true);
        b.HasIndex(x => new { x.ActorSubject, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => x.CreatedAt); // retention delete (spec §11: 90 days)
    }
}

internal sealed class DataExportRequestConfiguration : IEntityTypeConfiguration<DataExportRequest>
{
    public void Configure(EntityTypeBuilder<DataExportRequest> b)
    {
        b.ToTable("data_export_requests", t =>
        {
            t.EnumCheck<DataExportRequest, ExportStatus>("status");
            t.Check("ready_fields", "status <> 'ready' OR (storage_key IS NOT NULL AND download_expires_at IS NOT NULL)");
            t.Check("failed_fields", "(status = 'failed') = (failure_code IS NOT NULL)");
        });
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => new { x.PlayerId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => x.PlayerId).IsUnique().HasFilter("status IN ('queued', 'running')")
            .HasDatabaseName("ux_data_export_requests_one_pending_per_player");
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}

internal sealed class DeletionRequestConfiguration : IEntityTypeConfiguration<DeletionRequest>
{
    public void Configure(EntityTypeBuilder<DeletionRequest> b)
    {
        b.ToTable("deletion_requests", t => t.EnumCheck<DeletionRequest, DeletionStatus>("status"));
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.PlayerId);
    }
}

internal sealed class OutboxJobConfiguration : IEntityTypeConfiguration<OutboxJob>
{
    public void Configure(EntityTypeBuilder<OutboxJob> b)
    {
        b.ToTable("outbox_jobs", t =>
        {
            t.EnumCheck<OutboxJob, OutboxStatus>("status");
            t.Check("attempts", "attempts >= 0");
            t.Check("payload_object", "jsonb_typeof(payload) = 'object'");
        });
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.Payload).Jsonb();
        b.Property(x => x.Attempts).HasDefaultValue(0);
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.JobKey).IsUnique();
        b.HasIndex(x => new { x.Status, x.RunAfter });
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_records", t =>
        {
            t.EnumCheck<IdempotencyRecord, IdempotencyStatus>("status");
            t.Check("response_status", "response_status IS NULL OR response_status BETWEEN 100 AND 599");
            t.Check("completed_has_response", "status <> 'completed' OR response_status IS NOT NULL");
            // A completed record has no lease, so the takeover (WHERE locked_until < now()) can never match it.
            t.Check("lease_only_in_progress", "(status = 'in_progress') = (locked_until IS NOT NULL)");
        });
        b.HasKey(x => new { x.ActorSubject, x.Operation, x.Key });
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.ResponseBody).Jsonb();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.ExpiresAt);
    }
}
