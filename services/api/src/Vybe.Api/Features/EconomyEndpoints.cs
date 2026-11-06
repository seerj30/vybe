using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record CatalogItemResponse(
    Guid Id, string Sku, string Name, long Price, ItemSlot Slot, Rarity Rarity, int MinLevel, string PreviewUrl, bool Owned);

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record InventoryItemResponse(
    Guid ItemId, string Sku, string Name, ItemSlot Slot, Rarity Rarity, InventorySource Source, DateTime AcquiredAt,
    string PreviewUrl, IReadOnlyList<string> CompatibleBodyPresets);

public sealed record InventoryResponse(IReadOnlyList<InventoryItemResponse> Items);

public sealed record PurchaseRequest(Guid ItemId);

public sealed record PurchaseResponse(Guid TransactionId, Guid ItemId, long BalanceAfter);

public sealed record EquipRequest(ItemSlot Slot, Guid? ItemId, int? AttachIndex, long ExpectedAvatarRevision);

public sealed record EquipmentUpdated(long Revision, Equipped Equipped);

public sealed record LedgerEntryResponse(
    Guid Id, long Delta, long BalanceAfter, LedgerReason Reason, string? ReferenceType, Guid? ReferenceId, DateTime CreatedAt);

public static class EconomyEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/catalog/items", ListCatalogAsync).AllowAnonymous();
        api.MapGet("/me/inventory", GetInventoryAsync);
        api.MapPost("/purchases", PurchaseAsync);
        api.MapPost("/me/equipment", EquipAsync);
        api.MapGet("/me/wallet/transactions", ListLedgerAsync);
    }

    private static async Task<Page<CatalogItemResponse>> ListCatalogAsync(
        string? slot, string? cursor, int? limit, HttpContext context, VybeDbContext db, CurrentPlayer current,
        ContentCatalog content, TimeProvider clock, CancellationToken cancellationToken)
    {
        var take = Cursor.Limit(limit, 24);
        var after = ParseGuidCursor(cursor);
        var playerId = await current.TryGetIdAsync(cancellationToken);
        var now = clock.UtcNowForDb();

        var query = db.CatalogItems.AsNoTracking()
            .Where(i => i.IsActive && i.Price > 0
                && (i.AvailableFrom == null || i.AvailableFrom <= now)
                && (i.AvailableUntil == null || i.AvailableUntil > now));
        if (slot is not null)
        {
            // Query values use the API's snake_case enum spelling (e.g. "accessory").
            var parsed = TextEnums.Values<ItemSlot>().Contains(slot)
                ? TextEnums.FromDatabase<ItemSlot>(slot)
                : throw Problem.ValidationFailed("/slot", "invalid", "unknown slot");
            query = query.Where(i => i.Slot == parsed);
        }

        if (after is not null)
        {
            query = query.Where(i => i.Id.CompareTo(after.Value) > 0);
        }

        var items = await query.OrderBy(i => i.Id).Take(take + 1).ToListAsync(cancellationToken);
        var ids = items.Select(i => i.Id).ToList();
        var owned = playerId is null
            ? []
            : await db.PlayerInventory.Where(p => p.PlayerId == playerId && ids.Contains(p.ItemId)).Select(p => p.ItemId)
                .ToListAsync(cancellationToken);

        context.Response.Headers.CacheControl = "private, no-store"; // `owned` depends on the caller
        var page = items.Take(take)
            .Select(i => new CatalogItemResponse(i.Id, i.Sku, i.Name, i.Price, i.Slot, i.Rarity, i.MinLevel,
                content.PreviewUrl(i.AssetKey), owned.Contains(i.Id)))
            .ToList();
        return new Page<CatalogItemResponse>(page, items.Count > take ? Cursor.Encode(page[^1].Id.ToString()) : null);
    }

    private static async Task<InventoryResponse> GetInventoryAsync(
        VybeDbContext db, CurrentPlayer current, ContentCatalog content, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var rows = await db.PlayerInventory.AsNoTracking()
            .Where(p => p.PlayerId == playerId)
            .Join(db.CatalogItems, p => p.ItemId, i => i.Id, (p, i) => new { p, i })
            .OrderBy(x => x.p.AcquiredAt)
            .ToListAsync(cancellationToken);
        return new InventoryResponse(rows
            .Select(x => new InventoryItemResponse(x.i.Id, x.i.Sku, x.i.Name, x.i.Slot, x.i.Rarity, x.p.Source, x.p.AcquiredAt,
                content.PreviewUrl(x.i.AssetKey), x.i.CompatibleBodyPresets))
            .ToList());
    }

    private static async Task<IResult> PurchaseAsync(
        HttpContext context, VybeDbContext db, CurrentPlayer current, Idempotency idempotency, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var body = await RequestBody.ReadAsync(context, cancellationToken);
        var request = RequestBody.Deserialize<PurchaseRequest>(body);
        var hash = Idempotency.Hash("purchases.create", string.Empty, body);

        return await idempotency.RunAsync(context, playerId.ToString(), "purchases.create", hash, async ct =>
        {
            // Invariant 1 and 2: debit, ledger row and inventory grant commit together, under the wallet row lock.
            var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct);
            current.EnsureNotSuspended(player);
            var now = clock.UtcNowForDb();
            var item = await db.CatalogItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == request.ItemId, ct);
            if (item is null || !item.IsActive || item.AvailableFrom > now || item.AvailableUntil <= now)
            {
                throw Problem.Of(422, "item_unavailable");
            }

            if (item.Price == 0)
            {
                throw Problem.Of(422, "item_not_purchasable");
            }

            if (player.Level < item.MinLevel)
            {
                throw Problem.Of(403, "level_too_low");
            }

            var wallet = await db.Wallets.FromSql($"SELECT * FROM vybe.wallets WHERE player_id = {playerId} FOR UPDATE").SingleAsync(ct);
            if (await db.PlayerInventory.AnyAsync(p => p.PlayerId == playerId && p.ItemId == item.Id, ct))
            {
                throw Problem.Of(409, "already_owned");
            }

            if (wallet.Balance < item.Price)
            {
                throw Problem.Of(409, "insufficient_funds");
            }

            var entry = await RewardService.CreditAsync(db, playerId, -item.Price, LedgerReason.Purchase, "catalog_item", item.Id,
                $"purchase:{item.Id}", ct);
            db.PlayerInventory.Add(new PlayerInventoryItem
            {
                PlayerId = playerId,
                ItemId = item.Id,
                Source = InventorySource.Purchase,
                SourceTransactionId = entry.Id,
            });
            await db.SaveChangesAsync(ct);
            return new OperationResult(200, new PurchaseResponse(entry.Id, item.Id, entry.BalanceAfter));
        }, cancellationToken);
    }

    private static async Task<EquipmentUpdated> EquipAsync(
        EquipRequest request, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var playerId = await current.GetIdAsync(cancellationToken);
        var isAccessory = request.Slot == ItemSlot.Accessory;
        if (isAccessory != request.AttachIndex.HasValue || request.AttachIndex is not (null or 0 or 1))
        {
            throw Problem.ValidationFailed("/attachIndex", "invalid",
                "attachIndex (0 or 1) is required for slot accessory and forbidden otherwise");
        }

        var avatar = await db.AvatarConfigs.SingleAsync(a => a.PlayerId == playerId, cancellationToken);
        if (avatar.Revision != request.ExpectedAvatarRevision)
        {
            throw Problem.Of(409, "revision_conflict");
        }

        var equipped = Equipped.FromJson(avatar.EquippedJson);
        if (request.ItemId is { } itemId)
        {
            var item = await db.PlayerInventory.Where(p => p.PlayerId == playerId && p.ItemId == itemId)
                .Join(db.CatalogItems, p => p.ItemId, i => i.Id, (p, i) => i)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw Problem.Of(403, "item_not_owned");
            if (item.Slot != request.Slot || !item.CompatibleBodyPresets.Contains(avatar.BodyPresetId))
            {
                throw Problem.Of(422, "incompatible_item");
            }

            if (!item.IsActive)
            {
                throw Problem.Of(422, "item_inactive");
            }

            if (isAccessory && equipped.Accessories[1 - request.AttachIndex!.Value] == itemId)
            {
                throw Problem.Of(422, "item_already_equipped");
            }
        }

        avatar.EquippedJson = equipped.With(request.Slot, request.ItemId, request.AttachIndex).ToJson();
        await IdentityEndpoints.SaveWithRevisionCheckAsync(db, cancellationToken);
        return new EquipmentUpdated(avatar.Revision, Equipped.FromJson(avatar.EquippedJson));
    }

    private static async Task<Page<LedgerEntryResponse>> ListLedgerAsync(
        string? cursor, int? limit, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var take = Cursor.Limit(limit, 20);
        var query = db.WalletTransactions.AsNoTracking().Where(t => t.PlayerId == playerId);

        // Newest first: keyset on (created_at, id) descending.
        if (Cursor.Decode(cursor) is { } decoded)
        {
            var parts = decoded.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTime.MaxValue.Ticks || !Guid.TryParse(parts[1], out var lastId))
            {
                throw Problem.ValidationFailed("/cursor", "invalid_cursor", "cursor is not valid");
            }

            var lastCreated = new DateTime(ticks, DateTimeKind.Utc);
            query = query.Where(t => t.CreatedAt < lastCreated || (t.CreatedAt == lastCreated && t.Id.CompareTo(lastId) < 0));
        }

        var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take)
            .Select(t => new LedgerEntryResponse(t.Id, t.Delta, t.BalanceAfter, t.Reason, t.ReferenceType, t.ReferenceId, t.CreatedAt))
            .ToList();
        var next = rows.Count > take
            ? Cursor.Encode($"{page[^1].CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture)}|{page[^1].Id}")
            : null;
        return new Page<LedgerEntryResponse>(page, next);
    }

    private static Guid? ParseGuidCursor(string? cursor) =>
        Cursor.Decode(cursor) is not { } decoded
            ? null
            : Guid.TryParse(decoded, out var id) ? id : throw Problem.ValidationFailed("/cursor", "invalid_cursor", "cursor is not valid");
}
