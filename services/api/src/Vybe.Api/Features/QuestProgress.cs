using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

/// <summary>Daily quest rules of spec §8 and the atomic progress upsert of invariant 11.</summary>
public static class QuestProgress
{
    public const string Play3 = "play_3";
    public const string Score1500 = "score_1500";
    public const string ChallengeOrStreak = "challenge_or_streak";
    public const int ScoreThreshold = 1500;
    public const int StreakThreshold = 5;

    /// <summary>A completed quest stays claimable for 7 days after <c>period_start</c> (provisional, spec §17 item 4).</summary>
    public static readonly TimeSpan ClaimWindow = TimeSpan.FromDays(7);

    public static DateTime ClaimableUntil(DateOnly periodStart) => periodStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + ClaimWindow;

    /// <summary>
    /// The run's own quest progress, in the verification transaction (invariant 10). Only for a
    /// reward-eligible run; the quest day is the run's <c>reward_day</c>. Codes in a fixed order.
    /// </summary>
    public static async Task AddForRunAsync(
        VybeDbContext db, Guid playerId, DateOnly day, int score, int maxStreak, DateTime now, CancellationToken cancellationToken)
    {
        if (maxStreak >= StreakThreshold)
        {
            await AddAsync(db, playerId, ChallengeOrStreak, day, 1, now, cancellationToken);
        }

        await AddAsync(db, playerId, Play3, day, 1, now, cancellationToken);
        if (score >= ScoreThreshold)
        {
            await AddAsync(db, playerId, Score1500, day, 1, now, cancellationToken);
        }
    }

    /// <summary>Invariant 11: one atomic upsert for the active daily definition of <paramref name="code"/>; never read-modify-write.</summary>
    public static async Task AddAsync(
        VybeDbContext db, Guid playerId, string code, DateOnly day, int delta, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.player_quests (id, player_id, quest_id, period_start, target, progress, status, completed_at)
            SELECT {Guid.CreateVersion7()}, {playerId}, d.id, {day}, d.target, LEAST(d.target, {delta}),
                   CASE WHEN {delta} >= d.target THEN 'completed' ELSE 'active' END,
                   CASE WHEN {delta} >= d.target THEN {now} END
            FROM vybe.quest_definitions d
            WHERE d.code = {code} AND d.period = 'daily' AND d.is_active
            ORDER BY d.version DESC
            LIMIT 1
            ON CONFLICT (player_id, quest_id, period_start) DO UPDATE SET
              progress     = LEAST(player_quests.target, player_quests.progress + {delta}),
              status       = CASE WHEN player_quests.status = 'active'
                                   AND player_quests.progress + {delta} >= player_quests.target
                                  THEN 'completed' ELSE player_quests.status END,
              completed_at = CASE WHEN player_quests.status = 'active'
                                   AND player_quests.progress + {delta} >= player_quests.target
                                  THEN {now} ELSE player_quests.completed_at END
            """, cancellationToken);
    }
}

public sealed record QuestContent(string Code, int Version, QuestPeriod Period, JsonElement Criteria, int Target, long RewardCoins, int RewardXp);

public sealed record QuestFile(string? Description, QuestContent[] Quests);

/// <summary>Idempotent seeding of <c>assets/manifests/quests.v1.json</c>, like the catalog (Development, tests, deploy step).</summary>
public static class QuestSeeder
{
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static async Task SeedAsync(VybeDbContext db, string contentRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var path = Path.Combine(contentRoot, "assets", "manifests", "quests.v1.json");
        var file = JsonSerializer.Deserialize<QuestFile>(await File.ReadAllTextAsync(path, cancellationToken), FileJson)
            ?? throw new InvalidDataException($"content file {path} is empty");

        var existing = await db.QuestDefinitions.ToListAsync(cancellationToken);
        foreach (var definition in existing)
        {
            definition.IsActive = file.Quests.Any(q => q.Code == definition.Code && q.Version == definition.Version);
        }

        foreach (var quest in file.Quests)
        {
            var definition = existing.SingleOrDefault(d => d.Code == quest.Code && d.Version == quest.Version);
            if (definition is null)
            {
                definition = new QuestDefinition { Id = Guid.CreateVersion7(), Code = quest.Code, Criteria = "{}", IsActive = true };
                db.QuestDefinitions.Add(definition);
            }

            definition.Version = quest.Version;
            definition.Period = quest.Period;
            definition.Criteria = quest.Criteria.GetRawText();
            definition.Target = quest.Target;
            definition.RewardCoins = quest.RewardCoins;
            definition.RewardXp = quest.RewardXp;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
