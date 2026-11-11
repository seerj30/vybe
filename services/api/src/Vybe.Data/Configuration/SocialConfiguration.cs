using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vybe.Data.Entities;

namespace Vybe.Data.Configuration;

internal sealed class FriendshipConfiguration : IEntityTypeConfiguration<Friendship>
{
    public void Configure(EntityTypeBuilder<Friendship> b)
    {
        b.ToTable("friendships", t =>
        {
            t.EnumCheck<Friendship, FriendshipStatus>("status");
            t.Check("not_self", "requester_player_id <> addressee_player_id");
            t.Check("suppressed_pending", "NOT suppressed OR status = 'pending'");
            t.Check("responded_at", "(status = 'pending') = (responded_at IS NULL)");
        });
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.Suppressed).HasDefaultValue(false);
        b.Property(x => x.CreatedAt).DefaultNow();
        b.Property(x => x.UpdatedAt).DefaultNow();
        // The canonical pair is unique through ux_friendships_pair, an expression index on
        // (LEAST(requester, addressee), GREATEST(requester, addressee)) created in the migration.
        b.HasIndex(x => new { x.RequesterPlayerId, x.Status });
        b.HasIndex(x => new { x.AddresseePlayerId, x.Status });
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.RequesterPlayerId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.AddresseePlayerId);
    }
}

internal sealed class FriendRequestCounterConfiguration : IEntityTypeConfiguration<FriendRequestCounter>
{
    public void Configure(EntityTypeBuilder<FriendRequestCounter> b)
    {
        b.ToTable("friend_request_counters", t => t.Check("sent", "sent >= 0"));
        b.HasKey(x => new { x.PlayerId, x.Day });
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}

internal sealed class WeeklyLeaderboardScoreConfiguration : IEntityTypeConfiguration<WeeklyLeaderboardScore>
{
    public void Configure(EntityTypeBuilder<WeeklyLeaderboardScore> b)
    {
        b.ToTable("weekly_leaderboard_scores", t =>
        {
            t.Check("best_score", "best_score >= 0");
            t.Check("week_start_monday", "EXTRACT(ISODOW FROM week_start) = 1");
        });
        b.HasKey(x => new { x.GameId, x.WeekStart, x.PlayerId });
        b.HasIndex(x => new { x.GameId, x.WeekStart, x.BestScore, x.AchievedAt })
            .IsDescending(false, false, true, false)
            .HasDatabaseName("ix_weekly_leaderboard_scores_rank");
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
        b.HasOne<GameSession>().WithMany().HasForeignKey(x => x.SessionId);
    }
}
