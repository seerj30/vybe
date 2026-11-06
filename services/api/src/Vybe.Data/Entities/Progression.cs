namespace Vybe.Data.Entities;

// Spec v1.3 §4: quests, daily counters, achievements.

public sealed class QuestDefinition
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public int Version { get; set; }
    public QuestPeriod Period { get; set; }
    public required string Criteria { get; set; }
    public int Target { get; set; }
    public long RewardCoins { get; set; }
    public int RewardXp { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PlayerQuest
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public Guid QuestId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public int Target { get; set; }
    public int Progress { get; set; }
    public QuestStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
}

public sealed class PlayerDailyCounter
{
    public Guid PlayerId { get; set; }
    public DateOnly Day { get; set; }
    public int RunsRewarded { get; set; }
    public int XpFromGames { get; set; }
    public int ChallengeBonuses { get; set; }
}

public sealed class Achievement
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Criteria { get; set; }
    public Guid? RewardItemId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PlayerAchievement
{
    public Guid PlayerId { get; set; }
    public Guid AchievementId { get; set; }
    public DateTime UnlockedAt { get; set; }
}
