using Microsoft.EntityFrameworkCore;
using Vybe.Data;

namespace Vybe.Api.Features;

/// <summary>
/// The JSON document of a data export (spec §11 GDPR export): the player's own data. Reports against the
/// player stay restricted, and the block privacy rules apply (no <c>cancelled</c>, no suppressed challenge
/// for its recipient, no list of who blocked the player).
/// </summary>
public static class PlayerDataExport
{
    public const int FormatVersion = 1;

    public static async Task<object> BuildAsync(VybeDbContext db, Guid playerId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, cancellationToken);
        var avatar = await db.AvatarConfigs.AsNoTracking().SingleAsync(a => a.PlayerId == playerId, cancellationToken);
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(w => w.PlayerId == playerId, cancellationToken);

        return new
        {
            formatVersion = FormatVersion,
            exportedAt = now,
            player = new
            {
                player.Id,
                player.Handle,
                player.DisplayName,
                player.Kind,
                player.Status,
                player.Level,
                player.XpTotal,
                player.SuspendedUntil,
                player.SuspensionReason,
                player.CreatedAt,
            },
            avatar = new
            {
                avatar.BodyPresetId,
                avatar.SkinToneId,
                avatar.FacePresetId,
                avatar.HairPresetId,
                avatar.HairColorId,
                equipped = Equipped.FromJson(avatar.EquippedJson),
            },
            inventory = await db.PlayerInventory.AsNoTracking().Where(i => i.PlayerId == playerId)
                .Join(db.CatalogItems, i => i.ItemId, c => c.Id, (i, c) => new { itemId = c.Id, c.Sku, c.Name, i.Source, i.AcquiredAt })
                .ToListAsync(cancellationToken),
            outfits = (await db.SavedOutfits.AsNoTracking().Where(o => o.PlayerId == playerId).OrderBy(o => o.Slot).ToListAsync(cancellationToken))
                .Select(o => new { o.Id, o.Name, equipped = Equipped.FromJson(o.EquippedJson), o.CreatedAt, o.UpdatedAt }),
            wallet = new { wallet.Balance },
            ledger = await db.WalletTransactions.AsNoTracking().Where(t => t.PlayerId == playerId).OrderBy(t => t.CreatedAt)
                .Select(t => new { t.Id, t.Delta, t.BalanceAfter, t.Reason, t.ReferenceType, t.ReferenceId, t.CreatedAt })
                .ToListAsync(cancellationToken),
            gameSessions = await db.GameSessions.AsNoTracking().Where(s => s.PlayerId == playerId).OrderBy(s => s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.GameId,
                    s.RulesetId,
                    s.Status,
                    s.CreatedAt,
                    s.StartedAt,
                    s.SubmittedAt,
                    s.VerifiedAt,
                    s.Score,
                    s.RewardCoins,
                    s.RewardXp,
                    s.RejectionReason,
                    s.ChallengeId,
                })
                .ToListAsync(cancellationToken),
            challenges = (await db.Challenges.AsNoTracking()
                    .Where(c => c.CreatorPlayerId == playerId || (c.RecipientPlayerId == playerId && !c.Suppressed))
                    .OrderBy(c => c.CreatedAt)
                    .ToListAsync(cancellationToken))
                .Select(c => new
                {
                    c.Id,
                    role = c.CreatorPlayerId == playerId ? "creator" : "recipient",
                    status = c.Status == ChallengeStatus.Cancelled ? ChallengeStatus.Expired : c.Status,
                    won = c.WinnerPlayerId is null ? (bool?)null : c.WinnerPlayerId == playerId,
                    c.CreatedAt,
                    c.AcceptedAt,
                    c.ResolvedAt,
                }),
            quests = await db.PlayerQuests.AsNoTracking().Where(q => q.PlayerId == playerId).OrderBy(q => q.PeriodStart)
                .Join(db.QuestDefinitions, q => q.QuestId, d => d.Id, (q, d) => new
                {
                    q.Id,
                    d.Code,
                    q.PeriodStart,
                    q.Progress,
                    q.Target,
                    q.Status,
                    q.CompletedAt,
                    q.ClaimedAt,
                })
                .ToListAsync(cancellationToken),
            blockedPlayers = await db.PlayerBlocks.AsNoTracking().Where(b => b.BlockerPlayerId == playerId)
                .Join(db.Players, b => b.BlockedPlayerId, p => p.Id, (b, p) => new { p.Handle, blockedAt = b.CreatedAt })
                .ToListAsync(cancellationToken),
            reportsFiled = await db.PlayerReports.AsNoTracking().Where(r => r.ReporterPlayerId == playerId).OrderBy(r => r.CreatedAt)
                .Join(db.Players, r => r.TargetPlayerId, p => p.Id, (r, p) => new { r.Id, targetHandle = p.Handle, r.Reason, r.Details, r.Status, r.CreatedAt })
                .ToListAsync(cancellationToken),
        };
    }
}
