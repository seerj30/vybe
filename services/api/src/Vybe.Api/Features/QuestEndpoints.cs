using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record PlayerQuestResponse(
    Guid PlayerQuestId, string Code, DateOnly PeriodStart, int Progress, int Target, QuestStatus Status, long RewardCoins, int RewardXp,
    DateTime ClaimableUntil);

public sealed record QuestListResponse(IReadOnlyList<PlayerQuestResponse> Quests);

public sealed record QuestClaimed(long CoinsGranted, int XpGranted, int Level);

/// <summary>Spec §5 Quests, §6 Quest, §8 daily quests.</summary>
public static class QuestEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/me/quests", ListAsync);
        api.MapPost("/me/quests/{playerQuestId:guid}/claim", ClaimAsync);
    }

    /// <summary>
    /// Creates today's rows first (progress 0) so every quest has a <c>playerQuestId</c>; also lists
    /// completed, unclaimed quests of earlier days while claimable. The player row lock orders this
    /// writer with the run verification and the outbox, which take it first too.
    /// </summary>
    private static async Task<QuestListResponse> ListAsync(
        VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var now = clock.UtcNowForDb();
        var today = DateOnly.FromDateTime(now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE", cancellationToken);
        var daily = await db.QuestDefinitions.AsNoTracking()
            .Where(d => d.IsActive && d.Period == QuestPeriod.Daily)
            .OrderBy(d => d.Code)
            .Select(d => new { d.Id, d.Target })
            .ToListAsync(cancellationToken);
        foreach (var definition in daily)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vybe.player_quests (id, player_id, quest_id, period_start, target, progress, status)
                VALUES ({Guid.CreateVersion7()}, {playerId}, {definition.Id}, {today}, {definition.Target}, 0, 'active')
                ON CONFLICT (player_id, quest_id, period_start) DO NOTHING
                """, cancellationToken);
        }

        var oldestClaimable = DateOnly.FromDateTime(now - QuestProgress.ClaimWindow);
        var rows = await db.PlayerQuests.AsNoTracking()
            .Where(q => q.PlayerId == playerId
                && (q.PeriodStart == today || (q.Status == QuestStatus.Completed && q.PeriodStart >= oldestClaimable)))
            .Join(db.QuestDefinitions, q => q.QuestId, d => d.Id, (q, d) => new { q, d.Code, d.RewardCoins, d.RewardXp })
            .OrderByDescending(x => x.q.PeriodStart).ThenBy(x => x.Code)
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new QuestListResponse(rows
            .Where(x => x.q.PeriodStart == today || now < QuestProgress.ClaimableUntil(x.q.PeriodStart))
            .Select(x => new PlayerQuestResponse(x.q.Id, x.Code, x.q.PeriodStart, x.q.Progress, x.q.Target, x.q.Status, x.RewardCoins,
                x.RewardXp, QuestProgress.ClaimableUntil(x.q.PeriodStart)))
            .ToList());
    }

    private static async Task<IResult> ClaimAsync(
        Guid playerQuestId, HttpContext context, VybeDbContext db, CurrentPlayer current, Idempotency idempotency, TimeProvider clock,
        AchievementCatalog achievements, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var body = await RequestBody.ReadAsync(context, cancellationToken);
        var hash = Idempotency.Hash("quests.claim", playerQuestId.ToString(), body);
        return await idempotency.RunAsync(context, playerId.ToString(), "quests.claim", hash, async ct =>
        {
            var player = await db.Players.FromSql($"SELECT * FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE").SingleAsync(ct);
            current.EnsureNotSuspended(player);
            var quest = await db.PlayerQuests
                .FromSql($"SELECT * FROM vybe.player_quests WHERE id = {playerQuestId} AND player_id = {playerId} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
                ?? throw Problem.Of(404, "not_found");
            var now = clock.UtcNowForDb();
            switch (quest.Status)
            {
                case QuestStatus.Claimed:
                    throw Problem.Of(409, "quest_already_claimed");
                case QuestStatus.Active:
                    throw Problem.Of(409, "quest_not_completed");
            }

            if (now >= QuestProgress.ClaimableUntil(quest.PeriodStart))
            {
                throw Problem.Of(410, "quest_claim_expired");
            }

            var definition = await db.QuestDefinitions.AsNoTracking().SingleAsync(d => d.Id == quest.QuestId, ct);
            quest.Status = QuestStatus.Claimed;
            quest.ClaimedAt = now;
            if (definition.RewardCoins > 0)
            {
                await RewardService.CreditAsync(db, playerId, definition.RewardCoins, LedgerReason.QuestReward, "player_quest", quest.Id,
                    $"quest:{quest.Id}", ct);
            }

            // Quest XP is not limited by the daily gameplay cap (spec §8).
            await RewardService.AddXpAsync(db, player, definition.RewardXp, ct);
            await db.SaveChangesAsync(ct);
            await AchievementProgress.SyncCountsAsync(db, achievements, playerId, now, ct); // P1: quests_claimed
            return new OperationResult(200, new QuestClaimed(definition.RewardCoins, definition.RewardXp, player.Level));
        }, cancellationToken);
    }
}
