using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vybe.Data.Entities;

namespace Vybe.Data.Configuration;

internal sealed class QuestDefinitionConfiguration : IEntityTypeConfiguration<QuestDefinition>
{
    public void Configure(EntityTypeBuilder<QuestDefinition> b)
    {
        b.ToTable("quest_definitions", t =>
        {
            t.EnumCheck<QuestDefinition, QuestPeriod>("period");
            t.Check("target", "target > 0");
            t.Check("rewards", "reward_coins >= 0 AND reward_xp >= 0");
            t.Check("criteria_object", "jsonb_typeof(criteria) = 'object'");
        });
        b.Property(x => x.Period).HasTextEnum();
        b.Property(x => x.Criteria).Jsonb();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => new { x.Code, x.Version }).IsUnique();
    }
}

internal sealed class PlayerQuestConfiguration : IEntityTypeConfiguration<PlayerQuest>
{
    public void Configure(EntityTypeBuilder<PlayerQuest> b)
    {
        b.ToTable("player_quests", t =>
        {
            t.EnumCheck<PlayerQuest, QuestStatus>("status");
            t.Check("target", "target > 0");
            t.Check("progress", "progress BETWEEN 0 AND target");
            t.Check("completed_fields", "status = 'active' OR (progress = target AND completed_at IS NOT NULL)");
            t.Check("claimed_fields", "(status = 'claimed') = (claimed_at IS NOT NULL)");
        });
        b.Property(x => x.Status).HasTextEnum();
        b.Property(x => x.Progress).HasDefaultValue(0);
        b.HasIndex(x => new { x.PlayerId, x.QuestId, x.PeriodStart }).IsUnique();
        b.HasIndex(x => x.QuestId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
        b.HasOne<QuestDefinition>().WithMany().HasForeignKey(x => x.QuestId);
    }
}

internal sealed class PlayerDailyCounterConfiguration : IEntityTypeConfiguration<PlayerDailyCounter>
{
    public void Configure(EntityTypeBuilder<PlayerDailyCounter> b)
    {
        b.ToTable("player_daily_counters", t =>
            t.Check("non_negative", "runs_rewarded >= 0 AND xp_from_games >= 0 AND challenge_bonuses >= 0"));
        b.HasKey(x => new { x.PlayerId, x.Day });
        b.Property(x => x.RunsRewarded).HasDefaultValue(0);
        b.Property(x => x.XpFromGames).HasDefaultValue(0);
        b.Property(x => x.ChallengeBonuses).HasDefaultValue(0);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}

internal sealed class AchievementConfiguration : IEntityTypeConfiguration<Achievement>
{
    public void Configure(EntityTypeBuilder<Achievement> b)
    {
        b.ToTable("achievements", t =>
        {
            t.Check("criteria_object", "jsonb_typeof(criteria) = 'object'");
            t.Check("reward_xp", "reward_xp >= 0");
        });
        b.Property(x => x.Criteria).Jsonb();
        b.Property(x => x.CreatedAt).DefaultNow();
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.RewardItemId);
        b.HasOne<CatalogItem>().WithMany().HasForeignKey(x => x.RewardItemId);
    }
}

internal sealed class PlayerAchievementConfiguration : IEntityTypeConfiguration<PlayerAchievement>
{
    public void Configure(EntityTypeBuilder<PlayerAchievement> b)
    {
        b.ToTable("player_achievements");
        b.HasKey(x => new { x.PlayerId, x.AchievementId });
        b.Property(x => x.UnlockedAt).DefaultNow();
        b.HasIndex(x => x.AchievementId);
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
        b.HasOne<Achievement>().WithMany().HasForeignKey(x => x.AchievementId);
    }
}

internal sealed class PlayerStatConfiguration : IEntityTypeConfiguration<PlayerStat>
{
    public void Configure(EntityTypeBuilder<PlayerStat> b)
    {
        b.ToTable("player_stats", t =>
        {
            t.Check("value", "value >= 0");
            t.Check("stat", "stat ~ '^[a-z0-9_:-]{1,64}$'");
        });
        b.HasKey(x => new { x.PlayerId, x.Stat });
        b.Property(x => x.UpdatedAt).DefaultNow();
        b.HasOne<Player>().WithMany().HasForeignKey(x => x.PlayerId);
    }
}
