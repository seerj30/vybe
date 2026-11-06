using Microsoft.EntityFrameworkCore;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Gameplay;

/// <summary>
/// Row locks for one player's rows (spec §4 invariant 10, ADR 0002). Player and session rows use
/// <c>FOR NO KEY UPDATE</c> so foreign-key checks of other transactions do not wait; the wallet uses
/// <c>FOR UPDATE</c> (invariant 2; no foreign key references it). Callers lock in this order:
/// player, session, wallet.
/// </summary>
internal static class RowLocks
{
    public static async Task<Player> PlayerAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken) =>
        (await db.Players
            .FromSql($"SELECT * FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE")
            .ToListAsync(cancellationToken))
        .Single();

    public static async Task<GameSession?> SessionAsync(
        VybeDbContext db,
        Guid sessionId,
        Guid playerId,
        CancellationToken cancellationToken) =>
        (await db.GameSessions
            .FromSql($"SELECT * FROM vybe.game_sessions WHERE id = {sessionId} AND player_id = {playerId} FOR NO KEY UPDATE")
            .ToListAsync(cancellationToken))
        .SingleOrDefault();

    public static async Task<Wallet> WalletAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken) =>
        (await db.Wallets
            .FromSql($"SELECT * FROM vybe.wallets WHERE player_id = {playerId} FOR UPDATE")
            .ToListAsync(cancellationToken))
        .Single();
}
