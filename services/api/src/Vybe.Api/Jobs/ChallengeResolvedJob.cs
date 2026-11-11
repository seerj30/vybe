using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Jobs;

/// <summary>
/// <c>challenge-resolved:{challengeId}:{playerId}</c> (spec §6 Challenge, §7 challenge rules, invariant 10):
/// one transaction for one player. Grants the +50 winner bonus (ledger <c>challenge:{id}</c>, at most 5 per
/// UTC day of <c>resolved_at</c> via <c>player_daily_counters.challenge_bonuses</c>) and adds
/// <c>challenge_or_streak</c> progress on that day for both participants (target 1, so a retry cannot overcount).
/// A suspended player's job is rescheduled to the end of the suspension; a deleting or deleted player's job ends.
/// </summary>
public static class ChallengeResolvedJob
{
    public const string Kind = ChallengeRuns.ResolvedJobKind;
    public const long WinnerBonus = 50;
    public const int DailyBonusCap = 5;

    /// <summary>Retry delay while a suspension has no end; the job stays pending until it is lifted.</summary>
    public static readonly TimeSpan IndefiniteSuspensionRetry = TimeSpan.FromDays(1);

    public sealed record Payload(Guid ChallengeId, Guid PlayerId);

    /// <summary>Runs the job; returns null when it is done, or the time to run it again (suspended player).</summary>
    public static async Task<DateTime?> RunAsync(
        VybeDbContext db, AchievementCatalog catalog, Payload payload, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payload);
        var challenge = await db.Challenges.AsNoTracking().SingleAsync(c => c.Id == payload.ChallengeId, cancellationToken);
        if (challenge.Status != ChallengeStatus.Completed)
        {
            return null;
        }

        var player = await db.Players
            .FromSql($"SELECT * FROM vybe.players WHERE id = {payload.PlayerId} FOR NO KEY UPDATE")
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        // Deletion ends economic actions for good. Suspension only blocks them (spec §6): the bonus and
        // the quest progress wait until the suspension ends.
        if (player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted)
        {
            return null;
        }

        if (CurrentPlayer.IsSuspended(player, now))
        {
            return player.SuspendedUntil ?? now + IndefiniteSuspensionRetry;
        }

        var day = DateOnly.FromDateTime(challenge.ResolvedAt!.Value);
        var ledgerKey = $"challenge:{challenge.Id}";
        if (challenge.WinnerPlayerId == player.Id
            && !await db.WalletTransactions.AnyAsync(t => t.PlayerId == player.Id && t.LedgerKey == ledgerKey, cancellationToken))
        {
            var bonuses = (await db.Database.SqlQuery<int>($"""
                INSERT INTO vybe.player_daily_counters (player_id, day, challenge_bonuses)
                VALUES ({player.Id}, {day}, 1)
                ON CONFLICT (player_id, day) DO UPDATE SET challenge_bonuses = player_daily_counters.challenge_bonuses + 1
                RETURNING challenge_bonuses AS "Value"
                """).ToListAsync(cancellationToken)).Single();
            if (bonuses <= DailyBonusCap)
            {
                await RewardService.CreditAsync(db, player.Id, WinnerBonus, LedgerReason.ChallengeReward, "challenge", challenge.Id,
                    ledgerKey, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        await QuestProgress.AddAsync(db, player.Id, QuestProgress.ChallengeOrStreak, day, 1, now, cancellationToken);
        // P1 achievements: challenges completed, won and rematches (counts, so a retry changes nothing).
        await AchievementProgress.SyncCountsAsync(db, catalog, player.Id, now, cancellationToken);
        return null;
    }
}
