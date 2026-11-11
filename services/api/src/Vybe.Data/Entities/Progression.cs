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

    /// <summary>P1: version of the definition in <c>assets/manifests/achievements.v1.json</c>.</summary>
    public int Version { get; set; }

    /// <summary>P1: XP granted once at unlock (no V-Coins, decision 5).</summary>
    public int RewardXp { get; set; }

    /// <summary>P1: false when the code left the manifest; an inactive achievement does not unlock and is not listed.</summary>
    public bool IsActive { get; set; }
}

public sealed class PlayerAchievement
{
    public Guid PlayerId { get; set; }
    public Guid AchievementId { get; set; }
    public DateTime UnlockedAt { get; set; }
}

/// <summary>
/// P1 achievements (docs/design/p1-achievements.md): one server-computed value per player and stat (for example
/// <c>runs</c>, <c>best:street-hoops</c>). Written with atomic upserts in the transactions that change the counted data.
/// </summary>
public sealed class PlayerStat
{
    public Guid PlayerId { get; set; }
    public required string Stat { get; set; }
    public long Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A finished one-time content job, by name (P1: the achievements backfill). <c>Fingerprint</c> identifies the content
/// version it ran for; it is written only after the job completed, so a job that stopped half way runs again.
/// </summary>
public sealed class ContentMarker
{
    public required string Name { get; set; }
    public required string Fingerprint { get; set; }
    public DateTime CompletedAt { get; set; }
}
