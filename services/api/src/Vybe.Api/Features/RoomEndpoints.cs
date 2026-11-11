using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record RoomItemRef(Guid ItemId, string Sku);

public sealed record RoomPlacement(Guid ItemId, string Sku, string Surface, int X, int Y, int Rotation);

public sealed record RoomResponse(long Revision, RoomItemRef? WallItem, RoomItemRef? FloorItem, IReadOnlyList<RoomPlacement> Items, DateTime? UpdatedAt)
{
    /// <summary>The default room: no row yet, or what a blocked caller sees (the same answer, so a block stays private).</summary>
    public static RoomResponse Empty => new(0, null, null, [], null);

    /// <summary>
    /// What other players see: finishes and placements only. Revision and time stay private, so an emptied room, a
    /// never-decorated room and the room that a block hides give the same answer.
    /// </summary>
    public RoomResponse ForViewers() => this with { Revision = 0, UpdatedAt = null };
}

public sealed record RoomPlacementInput(Guid? ItemId, string? Surface, int? X, int? Y, int? Rotation);

public sealed record RoomLayoutRequest(long? ExpectedRevision, Guid? WallItemId, Guid? FloorItemId, RoomPlacementInput[]? Items);

/// <summary>
/// My Room (docs/design/p1-my-room.md): read the own room, save a validated layout, and read another
/// player's room with the profile visibility rules (spec §17 decision 6).
/// </summary>
public static class RoomEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/me/room", GetMineAsync);
        api.MapPut("/me/room", SaveAsync);
        // Anonymous callers are let through, so they get the 404 of an unknown handle (as the profile).
        api.MapGet("/profiles/{handle}/room", GetForProfileAsync).AllowAnonymous();
    }

    private static async Task<RoomResponse> GetMineAsync(VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        return await ReadAsync(db, playerId, cancellationToken);
    }

    /// <summary>
    /// The same 404 as <c>GET /profiles/{handle}</c> for anonymous callers, unknown or deleted handles and private
    /// profiles of others. A block in either direction gives the empty default room, not a 404: the profile stays
    /// visible (ADR 0005), so a 404 here would tell the caller about the block.
    /// </summary>
    private static async Task<IResult> GetForProfileAsync(
        string handle, HttpContext context, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Robots-Tag"] = "noindex";
        var callerId = await current.TryGetIdAsync(cancellationToken);
        var owner = callerId is null ? null : await PlayerHandles.FindAsync(db, handle, cancellationToken);
        if (owner is null || (owner.ProfileVisibility == ProfileVisibility.Private && owner.Id != callerId))
        {
            await ProblemWriter.WriteAsync(context, 404, "not_found");
            return Results.Empty;
        }

        // The same queries for every caller (the room is read also when a block hides it), so the time does not tell either.
        var room = (await ReadAsync(db, owner.Id, cancellationToken)).ForViewers();
        var blocked = owner.Id != callerId && await db.PlayerBlocks.AsNoTracking().AnyAsync(
            b => (b.BlockerPlayerId == owner.Id && b.BlockedPlayerId == callerId)
                || (b.BlockerPlayerId == callerId && b.BlockedPlayerId == owner.Id),
            cancellationToken);
        return TypedResults.Ok(blocked ? RoomResponse.Empty : room);
    }

    /// <summary>
    /// <c>PUT /me/room</c>: replaces the whole room, all or nothing, under a row lock. A save equal to the stored room
    /// returns it unchanged (idempotent retry); another layout with an old revision is 409 <c>revision_conflict</c>.
    /// </summary>
    private static async Task<RoomResponse> SaveAsync(
        HttpContext context, VybeDbContext db, CurrentPlayer current, ContentCatalog content, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var request = RequestBody.Deserialize<RoomLayoutRequest>(await RequestBody.ReadAsync(context, cancellationToken));
        var placements = Shape(request, content.Room);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // FOR SHARE waits for a running deletion (it holds FOR NO KEY UPDATE), so no room row survives the anonymization.
        var player = await db.Players.FromSql($"SELECT * FROM vybe.players WHERE id = {playerId} FOR SHARE").AsNoTracking()
            .SingleAsync(cancellationToken);
        if (player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted)
        {
            throw Problem.Unauthenticated();
        }

        current.EnsureNotSuspended(player);
        // The empty row first, so two first saves with expectedRevision 0 meet on the same row lock.
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO vybe.player_rooms (player_id) VALUES ({playerId}) ON CONFLICT (player_id) DO NOTHING", cancellationToken);
        var room = await db.PlayerRooms.FromSql($"SELECT * FROM vybe.player_rooms WHERE player_id = {playerId} FOR UPDATE")
            .SingleAsync(cancellationToken);

        var layoutJson = RoomLayout.ToJson(placements);
        var same = room.WallItemId == request.WallItemId && room.FloorItemId == request.FloorItemId
            && RoomLayout.ToJson(RoomLayout.Parse(room.LayoutJson)) == layoutJson;
        if (same)
        {
            // Nothing changes. An empty first save keeps no placeholder row (the transaction rolls back).
            if (room.Revision > 0)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return room.Revision > 0 ? await ReadAsync(db, playerId, cancellationToken) : RoomResponse.Empty;
        }

        if (room.Revision != request.ExpectedRevision)
        {
            throw Problem.Of(409, "revision_conflict");
        }

        var ids = placements.Select(p => p.ItemId)
            .Concat(new[] { request.WallItemId, request.FloorItemId }.Where(id => id.HasValue).Select(id => id!.Value))
            .Distinct()
            .ToList();
        var owned = await db.PlayerInventory.AsNoTracking()
            .Where(p => p.PlayerId == playerId && ids.Contains(p.ItemId))
            .Join(db.CatalogItems, p => p.ItemId, i => i.Id, (p, i) => i)
            .ToDictionaryAsync(i => i.Id, cancellationToken);
        if (ids.FirstOrDefault(id => !owned.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            throw RoomLayout.ItemProblem(403, "item_not_owned", missing);
        }

        CheckFinish(request.WallItemId, ItemSlot.RoomWall, owned);
        CheckFinish(request.FloorItemId, ItemSlot.RoomFloor, owned);
        var items = owned.Values.ToDictionary(
            i => i.Id,
            i => new PlaceableItem(i.Id, i.Slot, i.IsActive, content.RoomItems.GetValueOrDefault(i.Sku)));
        RoomLayout.Validate(placements, items, content.Room);

        room.WallItemId = request.WallItemId;
        room.FloorItemId = request.FloorItemId;
        room.LayoutJson = layoutJson;
        room.UpdatedAt = clock.UtcNowForDb();
        await IdentityEndpoints.SaveWithRevisionCheckAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadAsync(db, playerId, cancellationToken);
    }

    /// <summary>Request shape (before any database read): required fields, item limit, surfaces, rotations, duplicates.</summary>
    private static List<StoredPlacement> Shape(RoomLayoutRequest request, RoomFile room)
    {
        if (request.ExpectedRevision is not { } revision || revision < 0)
        {
            throw Problem.ValidationFailed("/expectedRevision", "required", "expectedRevision (0 or more) is required");
        }

        if (request.Items is null)
        {
            throw Problem.ValidationFailed("/items", "required", "items is required");
        }

        if (request.Items.Length > room.MaxItems)
        {
            throw Problem.Of(422, "room_item_limit", $"a room holds at most {room.MaxItems} items");
        }

        var seen = new HashSet<Guid>();
        var placements = new List<StoredPlacement>(request.Items.Length);
        foreach (var input in request.Items)
        {
            if (input?.ItemId is not { } itemId || itemId == Guid.Empty || input.X is not { } x || input.Y is not { } y
                || input.Rotation is not { } rotation || input.Surface is not { } surface)
            {
                throw Problem.ValidationFailed("/items", "required", "every item needs itemId, surface, x, y and rotation");
            }

            if (!RoomLayout.Surfaces.Contains(surface))
            {
                throw Problem.ValidationFailed("/items/surface", "invalid", "surface must be floor, wall_back or wall_left");
            }

            if (x < 0 || y < 0 || x >= RoomLayout.MaxCoordinate || y >= RoomLayout.MaxCoordinate)
            {
                // Before any sum with the footprint: a huge value must not overflow (checked arithmetic → 500).
                throw RoomLayout.ItemProblem(422, "placement_out_of_bounds", itemId);
            }

            if (!RoomLayout.Rotations.Contains(rotation))
            {
                throw Problem.ValidationFailed("/items/rotation", "invalid", "rotation must be 0, 90, 180 or 270");
            }

            if (!seen.Add(itemId))
            {
                throw Problem.ValidationFailed("/items/itemId", "duplicate", "an item can be placed once");
            }

            placements.Add(new StoredPlacement(itemId, surface, x, y, rotation));
        }

        return placements;
    }

    private static void CheckFinish(Guid? itemId, ItemSlot slot, Dictionary<Guid, CatalogItem> owned)
    {
        if (itemId is not { } id)
        {
            return;
        }

        var item = owned[id];
        if (item.Slot != slot)
        {
            throw RoomLayout.ItemProblem(422, "incompatible_item", id);
        }

        if (!item.IsActive)
        {
            throw RoomLayout.ItemProblem(422, "item_inactive", id);
        }
    }

    private static async Task<RoomResponse> ReadAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken)
    {
        var room = await db.PlayerRooms.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId, cancellationToken);
        if (room is null)
        {
            return RoomResponse.Empty;
        }

        var placements = RoomLayout.Parse(room.LayoutJson);
        var ids = placements.Select(p => p.ItemId)
            .Concat(new[] { room.WallItemId, room.FloorItemId }.Where(id => id.HasValue).Select(id => id!.Value))
            .Distinct()
            .ToList();
        var skus = await db.CatalogItems.AsNoTracking().Where(i => ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Sku, cancellationToken);
        RoomItemRef? Ref(Guid? id) => id is { } value && skus.TryGetValue(value, out var sku) ? new RoomItemRef(value, sku) : null;
        return new RoomResponse(
            room.Revision,
            Ref(room.WallItemId),
            Ref(room.FloorItemId),
            placements.Where(p => skus.ContainsKey(p.ItemId))
                .Select(p => new RoomPlacement(p.ItemId, skus[p.ItemId], p.Surface, p.X, p.Y, p.Rotation))
                .ToList(),
            room.UpdatedAt);
    }
}
