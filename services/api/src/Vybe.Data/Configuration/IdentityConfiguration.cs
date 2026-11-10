using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vybe.Data.Entities;

namespace Vybe.Data.Configuration;

internal sealed class PlayerConfiguration : IEntityTypeConfiguration<Player>
{
    public void Configure(EntityTypeBuilder<Player> b)
    {
        b.ToTable("players", t =>
        {
            t.EnumCheck<Player, PlayerKind>("kind");
            t.EnumCheck<Player, PlayerStatus>("status");
            t.Check("level", "level BETWEEN 1 AND 50");
            t.Check("xp_total", "xp_total >= 0");
            t.Check("handle_format", "handle ~ '^[a-z0-9_]{3,20}$'");
            t.Check("handle_normalized", "handle_normalized = handle");
            // J4 anonymization replaces the handle with the reserved pseudonym d_ + 18 characters.
            t.Check("deleted_pseudonym", "(status = 'deleted') = (handle ~ '^d_[a-z0-9]{18}$')");
            t.Check("deleted_at", "(status = 'deleted') = (deleted_at IS NOT NULL)");
            t.Check("auth_subject_by_kind", "status = 'deleted' OR (kind = 'guest') = (auth_subject IS NULL)");
            t.EnumCheck<Player, ProfileVisibility>("profile_visibility");
            t.EnumCheck<Player, AgeBand>("age_band");
            // The gate result is complete or absent (players created before the gate have none).
            t.Check("age_confirmation", "(age_confirmed_at IS NULL) = (age_policy_version IS NULL) AND (age_confirmed_at IS NULL) = (age_band IS NULL)");
        });
        b.Property(x => x.Kind).HasTextEnum();
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.DisplayName).HasMaxLength(40);
        // Sentinel -1: EF always sends the CLR value, so a wrong 0 hits the CHECK instead of becoming 1.
        b.Property(x => x.Level).HasDefaultValue(1).HasSentinel(-1);
        b.Property(x => x.XpTotal).HasDefaultValue(0L);
        b.Property(x => x.ProfileVisibility).HasTextEnum().HasDefaultValueSql("'players'");
        b.Property(x => x.AgeBand).HasTextEnum();
        b.Property(x => x.AgePolicyVersion).HasMaxLength(32);
        b.Property(x => x.CreatedAt).DefaultNow();
        b.Property(x => x.UpdatedAt).DefaultNow();
        b.HasIndex(x => x.AuthSubject).IsUnique();
        b.HasIndex(x => x.HandleNormalized).IsUnique();
        b.HasIndex(x => x.SuspendedUntil).HasFilter("status = 'suspended'")
            .HasDatabaseName("ix_players_suspended_until");
    }
}

internal sealed class GuestSessionConfiguration : IEntityTypeConfiguration<GuestSession>
{
    public void Configure(EntityTypeBuilder<GuestSession> b)
    {
        b.ToTable("guest_sessions", t => t.Check("token_hash_sha256", "octet_length(token_hash) = 32"));
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.PlayerId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}

internal sealed class AuthUserDeletionConfiguration : IEntityTypeConfiguration<AuthUserDeletion>
{
    public void Configure(EntityTypeBuilder<AuthUserDeletion> b)
    {
        b.ToTable("auth_user_deletions", t => t.Check("attempts", "attempts >= 0"));
        b.HasKey(x => x.AuthSubject);
        b.Property(x => x.Attempts).HasDefaultValue(0);
        b.Property(x => x.NextAttemptAt).DefaultNow();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.NextAttemptAt);
    }
}

internal sealed class AvatarConfigConfiguration : IEntityTypeConfiguration<AvatarConfig>
{
    public void Configure(EntityTypeBuilder<AvatarConfig> b)
    {
        b.ToTable("avatar_configs", t =>
        {
            t.Check("revision", "revision >= 0");
            t.Check("equipped_json_shape", ConfigurationHelpers.EquippedJsonShape);
        });
        b.HasKey(x => x.PlayerId);
        b.Property(x => x.EquippedJson).Jsonb();
        b.Property(x => x.Revision).IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasDefaultValue(0L);
        b.Property(x => x.UpdatedAt).DefaultNow();
        b.HasOne<Player>().WithOne().HasForeignKey<AvatarConfig>(x => x.PlayerId);
    }
}

internal sealed class PlayerBlockConfiguration : IEntityTypeConfiguration<PlayerBlock>
{
    public void Configure(EntityTypeBuilder<PlayerBlock> b)
    {
        b.ToTable("player_blocks", t => t.Check("not_self", "blocker_player_id <> blocked_player_id"));
        b.HasKey(x => new { x.BlockerPlayerId, x.BlockedPlayerId });
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.BlockedPlayerId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.BlockerPlayerId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.BlockedPlayerId);
    }
}

internal sealed class PlayerReportConfiguration : IEntityTypeConfiguration<PlayerReport>
{
    public void Configure(EntityTypeBuilder<PlayerReport> b)
    {
        b.ToTable("player_reports", t =>
        {
            t.EnumCheck<PlayerReport, ReportReason>("reason");
            t.EnumCheck<PlayerReport, ReportStatus>("status");
            t.Check("not_self", "reporter_player_id <> target_player_id");
        });
        b.Property(x => x.Reason).HasTextEnum();
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.Details).HasMaxLength(500);
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.ReporterPlayerId);
        b.HasIndex(x => x.TargetPlayerId);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.ReporterPlayerId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.TargetPlayerId);
    }
}
