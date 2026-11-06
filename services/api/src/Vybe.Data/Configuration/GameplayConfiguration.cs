using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vybe.Data.Entities;

namespace Vybe.Data.Configuration;

internal sealed class GameRulesetConfiguration : IEntityTypeConfiguration<GameRuleset>
{
    public void Configure(EntityTypeBuilder<GameRuleset> b)
    {
        b.ToTable("game_rulesets", t =>
        {
            t.Check("version", "version >= 1");
            t.Check("checksum_format", "checksum ~ '^[0-9a-f]{64}$'");
            t.Check("config_object", "jsonb_typeof(config) = 'object'");
        });
        b.Property(x => x.Config).Jsonb();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => new { x.GameId, x.Version }).IsUnique();
    }
}

internal sealed class GameSessionConfiguration : IEntityTypeConfiguration<GameSession>
{
    public void Configure(EntityTypeBuilder<GameSession> b)
    {
        b.ToTable("game_sessions", t =>
        {
            t.EnumCheck<GameSession, GameSessionStatus>("status");
            t.EnumCheck<GameSession, SessionRejectionReason>("rejection_reason");
            t.Check("seed_uint32", "seed BETWEEN 0 AND 4294967295");
            t.Check("non_negative",
                "COALESCE(score, 0) >= 0 AND COALESCE(event_count, 0) >= 0 AND reward_coins >= 0 AND reward_xp >= 0");
            t.Check("rejected_has_reason", "(status = 'rejected') = (rejection_reason IS NOT NULL)");
            t.Check("started_fields",
                "status NOT IN ('active', 'submitted', 'verified') OR (started_at IS NOT NULL AND submit_deadline IS NOT NULL)");
            t.Check("verified_fields",
                "status <> 'verified' OR (score IS NOT NULL AND verified_at IS NOT NULL AND reward_day IS NOT NULL)");
            t.Check("reward_day_utc", "reward_day IS NULL OR reward_day = (started_at AT TIME ZONE 'UTC')::date");
        });
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.RejectionReason).HasTextEnum();
        b.Property(x => x.ReviewFlags).HasDefaultValueSql("'{}'::text[]");
        b.Property(x => x.RewardCoins).HasDefaultValue(0L);
        b.Property(x => x.RewardXp).HasDefaultValue(0);
        b.Property(x => x.CreatedAt).DefaultNow();
        b.Property(x => x.Version).IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasDefaultValue(0L);

        // One recipient session per challenge; one open session per player (spec §4).
        b.HasIndex(x => x.ChallengeId).IsUnique().HasFilter("challenge_id IS NOT NULL");
        b.HasIndex(x => x.PlayerId).IsUnique().HasFilter("status IN ('created', 'active')")
            .HasDatabaseName("ux_game_sessions_one_open_per_player");
        b.HasIndex(x => new { x.PlayerId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => x.RulesetId);
        // Sweeper lookups.
        b.HasIndex(x => x.StartDeadline).HasFilter("status = 'created'")
            .HasDatabaseName("ix_game_sessions_created_start_deadline");
        b.HasIndex(x => x.SubmitDeadline).HasFilter("status = 'active'")
            .HasDatabaseName("ix_game_sessions_active_submit_deadline");

        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
        b.HasOne<GameRuleset>().WithMany().HasForeignKey(x => x.RulesetId);
        b.HasOne<Challenge>().WithMany().HasForeignKey(x => x.ChallengeId);
    }
}

internal sealed class GameInputTraceConfiguration : IEntityTypeConfiguration<GameInputTrace>
{
    public void Configure(EntityTypeBuilder<GameInputTrace> b)
    {
        b.ToTable("game_input_traces", t =>
        {
            t.Check("event_count", "event_count >= 0");
            t.Check("size_cap", "octet_length(compressed_trace) <= 16384");
        });
        b.HasKey(x => x.SessionId);
        b.Property(x => x.ReceivedAt).DefaultNow();
        b.HasIndex(x => x.ReceivedAt); // retention delete (spec §11: 30 days)
        b.HasOne<GameSession>().WithOne().HasForeignKey<GameInputTrace>(x => x.SessionId);
    }
}

internal sealed class ChallengeConfiguration : IEntityTypeConfiguration<Challenge>
{
    public void Configure(EntityTypeBuilder<Challenge> b)
    {
        b.ToTable("challenges", t =>
        {
            t.EnumCheck<Challenge, ChallengeStatus>("status");
            t.Check("not_self", "recipient_player_id IS NULL OR recipient_player_id <> creator_player_id");
            t.Check("token_hash_sha256", "octet_length(invite_token_hash) = 32");
            t.Check("accepted_fields",
                "status NOT IN ('accepted', 'completed') OR (recipient_player_id IS NOT NULL AND accepted_at IS NOT NULL AND complete_deadline IS NOT NULL)");
            t.Check("completed_fields",
                "status <> 'completed' OR (recipient_session_id IS NOT NULL AND resolved_at IS NOT NULL)");
            t.Check("winner_is_participant",
                "winner_player_id IS NULL OR winner_player_id = creator_player_id OR winner_player_id = recipient_player_id");
            t.Check("winner_only_when_completed", "winner_player_id IS NULL OR status = 'completed'");
            t.Check("distinct_sessions", "recipient_session_id IS NULL OR recipient_session_id <> creator_session_id");
        });
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.Suppressed).HasDefaultValue(false);
        b.Property(x => x.CreatedAt).DefaultNow();
        b.Property(x => x.Version).IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasDefaultValue(0L);

        b.HasIndex(x => x.InviteTokenHash).IsUnique();
        b.HasIndex(x => x.CreatorSessionId).IsUnique();
        b.HasIndex(x => x.RecipientSessionId).IsUnique().HasFilter("recipient_session_id IS NOT NULL");
        b.HasIndex(x => new { x.RematchOfChallengeId, x.CreatorPlayerId }).IsUnique()
            .HasFilter("rematch_of_challenge_id IS NOT NULL");
        b.HasIndex(x => new { x.CreatorPlayerId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.RecipientPlayerId, x.Status });
        b.HasIndex(x => new { x.Status, x.AcceptDeadline });
        b.HasIndex(x => x.CompleteDeadline).HasFilter("status = 'accepted'")
            .HasDatabaseName("ix_challenges_accepted_complete_deadline");
        b.HasIndex(x => x.WinnerPlayerId);
        b.HasIndex(x => x.RulesetId);

        b.HasOne<Player>().WithMany().HasForeignKey(x => x.CreatorPlayerId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.RecipientPlayerId);
        // No FK on winner_player_id: its check would take a KEY SHARE lock on the other player's
        // row inside the recipient's verification and deadlock crossed challenges (invariant 10).
        // ck_challenges_winner_is_participant already restricts it to the two participants.

        // A session belongs to its player and ruleset: the creator/recipient run must match both.
        b.HasOne<GameSession>().WithMany()
            .HasForeignKey(x => new { x.CreatorSessionId, x.CreatorPlayerId, x.RulesetId })
            .HasPrincipalKey(s => new { s.Id, s.PlayerId, s.RulesetId })
            .HasConstraintName("fk_challenges_creator_session");
        b.HasOne<GameSession>().WithMany()
            .HasForeignKey(x => new { x.RecipientSessionId, x.RecipientPlayerId, x.RulesetId })
            .HasPrincipalKey(s => new { s.Id, s.PlayerId, s.RulesetId })
            .HasConstraintName("fk_challenges_recipient_session");
        b.HasOne<GameRuleset>().WithMany().HasForeignKey(x => x.RulesetId);
        b.HasOne<Challenge>().WithMany().HasForeignKey(x => x.RematchOfChallengeId);
    }
}
