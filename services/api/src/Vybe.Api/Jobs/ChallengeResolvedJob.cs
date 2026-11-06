using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Jobs;

/// <summary>
/// <c>challenge-resolved:{challengeId}:{playerId}</c> (spec §6 Challenge, §7 challenge rules, invariant 10):
/// one transaction for one player. Grants the +50 winner bonus (ledger <c>challenge:{id}</c>, at most 5 per
/// UTC day of <c>resolved_at</c> via <c>player_daily_counters.challenge_bonuses</c>).
/// </summary>
public static class ChallengeResolvedJob
{
    public const string Kind = ChallengeRuns.ResolvedJobKind;
    public const long WinnerBonus = 50;
    public const int DailyBonusCap = 5;

    public sealed record Payload(Guid ChallengeId, Guid PlayerId);

    public static async Task RunAsync(VybeDbContext db, Payload payload, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payload);
        var challenge = await db.Challenges.AsNoTracking().SingleAsync(c => c.Id == payload.ChallengeId, cancellationToken);
        if (challenge.Status != ChallengeStatus.Completed)
        {
            return;
        }

        var player = await db.Players
            .FromSql($"SELECT * FROM vybe.players WHERE id = {payload.PlayerId} FOR NO KEY UPDATE")
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        // Suspension blocks economic actions (spec §6); deletion ends them.
        if (player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted || CurrentPlayer.IsSuspended(player, now))
        {
            return;
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
    }
}
