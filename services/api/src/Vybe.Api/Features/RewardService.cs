using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;
using Vybe.GameRules;

namespace Vybe.Api.Features;

/// <summary>
/// Rewards for one verified run (spec §7, invariant 10). Runs inside the submit transaction and locks
/// rows of this player only: the player row (FOR NO KEY UPDATE), the day's counter row, the day's quest
/// rows and the wallet.
/// </summary>
public static class RewardService
{
    // Mapped by the snake_case naming convention: runs_rewarded, xp_from_games.
    private sealed record CounterRow(int RunsRewarded, int XpFromGames);

    public static async Task<RewardsResponse> GrantForRunAsync(
        VybeDbContext db, GameSession session, StreetHoopsConfig config, int score, int eventCount, int maxStreak, DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(config);

        var player = await db.Players
            .FromSql($"SELECT * FROM vybe.players WHERE id = {session.PlayerId} FOR NO KEY UPDATE")
            .SingleAsync(cancellationToken);

        // Suspension blocks economic actions (spec §6), including a session opened before the suspension.
        if (player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted || CurrentPlayer.IsSuspended(player, now))
        {
            throw Problem.Of(403, "account_suspended");
        }

        // Minimum play (spec §7): a short run is verified but earns nothing and does not count.
        if (eventCount < config.MinShotsForRewards)
        {
            return new RewardsResponse(0, 0, player.Level);
        }

        var day = DateOnly.FromDateTime(session.StartedAt!.Value);
        var counter = (await db.Database.SqlQuery<CounterRow>($"""
            INSERT INTO vybe.player_daily_counters (player_id, day, runs_rewarded)
            VALUES ({player.Id}, {day}, 1)
            ON CONFLICT (player_id, day) DO UPDATE SET runs_rewarded = player_daily_counters.runs_rewarded + 1
            RETURNING runs_rewarded, xp_from_games
            """).ToListAsync(cancellationToken)).Single();

        // A reward-eligible run counts for quests whatever its coin tier (spec §7 minimum play, §8).
        await QuestProgress.AddForRunAsync(db, player.Id, day, score, maxStreak, now, cancellationToken);

        var coins = Rewards.CoinsForRun(Rewards.BaseCoins(score), counter.RunsRewarded);
        var xp = Rewards.XpGrant(Rewards.BaseXp(score), counter.XpFromGames);
        if (xp > 0)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE vybe.player_daily_counters SET xp_from_games = xp_from_games + {xp}
                WHERE player_id = {player.Id} AND day = {day}
                """, cancellationToken);
        }

        if (coins > 0)
        {
            await CreditAsync(db, player.Id, coins, LedgerReason.GameReward, "game_session", session.Id, $"game:{session.Id}", cancellationToken);
        }

        await AddXpAsync(db, player, xp, cancellationToken);
        return new RewardsResponse(coins, xp, player.Level);
    }

    /// <summary>
    /// Adds XP to a player row locked by the caller, updates the cached level and grants 100 V-Coins
    /// once per new level (spec §8, ledger <c>level:{L}</c>).
    /// </summary>
    public static async Task AddXpAsync(VybeDbContext db, Player player, int xp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        var oldLevel = player.Level;
        player.XpTotal += xp;
        player.Level = Rewards.LevelFor(player.XpTotal);
        for (var level = oldLevel + 1; level <= player.Level; level++)
        {
            await CreditAsync(db, player.Id, Rewards.LevelUpCoins, LedgerReason.LevelReward, "level", null, $"level:{level}", cancellationToken);
        }
    }

    /// <summary>Adds a ledger row and moves the wallet balance; the wallet row is locked for the transaction.</summary>
    public static async Task<WalletTransaction> CreditAsync(
        VybeDbContext db, Guid playerId, long delta, LedgerReason reason, string? referenceType, Guid? referenceId, string ledgerKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var wallet = await db.Wallets
            .FromSql($"SELECT * FROM vybe.wallets WHERE player_id = {playerId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        wallet.Balance += delta;
        var entry = new WalletTransaction
        {
            Id = Guid.CreateVersion7(),
            PlayerId = playerId,
            Delta = delta,
            BalanceAfter = wallet.Balance,
            Reason = reason,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            LedgerKey = ledgerKey,
        };
        db.WalletTransactions.Add(entry);
        return entry;
    }
}
