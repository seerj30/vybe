using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record OutfitNameRequest(string? Name);

public sealed record OutfitApplyRequest(long? ExpectedAvatarRevision);

public sealed record OutfitResponse(Guid Id, string Name, Equipped Equipped, bool Valid, IReadOnlyList<Guid> InvalidItemIds);

public sealed record OutfitList(IReadOnlyList<OutfitResponse> Outfits);

public sealed record OutfitCreated(Guid Id);

/// <summary>Spec §4 <c>saved_outfits</c>, §5 outfits: at most 3 per player, all-or-nothing apply.</summary>
public static class OutfitEndpoints
{
    public const int MaxOutfits = 3;

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/me/outfits", ListAsync);
        api.MapPost("/me/outfits", SaveAsync);
        api.MapPut("/me/outfits/{outfitId:guid}", RenameAsync);
        api.MapDelete("/me/outfits/{outfitId:guid}", DeleteAsync);
        api.MapPost("/me/outfits/{outfitId:guid}/apply", ApplyAsync);
    }

    private static async Task<OutfitList> ListAsync(VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var outfits = await db.SavedOutfits.AsNoTracking().Where(o => o.PlayerId == playerId).OrderBy(o => o.Slot).ToListAsync(cancellationToken);
        var bodyPreset = await BodyPresetAsync(db, playerId, cancellationToken);
        var result = new List<OutfitResponse>();
        foreach (var outfit in outfits)
        {
            result.Add(await ToResponseAsync(db, playerId, bodyPreset, outfit, cancellationToken));
        }

        return new OutfitList(result);
    }

    /// <summary>
    /// Saves the current equipment in the lowest free slot. The player row lock serializes saves of one
    /// player; <c>UNIQUE(player_id, slot)</c> and the slot CHECK are the database guard of the limit.
    /// </summary>
    private static async Task<IResult> SaveAsync(
        OutfitNameRequest request, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        var name = ValidName(request);
        var playerId = await current.GetIdAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE", cancellationToken);
        var used = await db.SavedOutfits.Where(o => o.PlayerId == playerId).Select(o => o.Slot).ToListAsync(cancellationToken);
        var slot = Enumerable.Range(1, MaxOutfits).Select(s => (short)s).Where(s => !used.Contains(s)).DefaultIfEmpty((short)0).First();
        if (slot == 0)
        {
            throw Problem.Of(409, "outfit_limit_reached");
        }

        var now = clock.UtcNowForDb();
        var outfit = new SavedOutfit
        {
            Id = Guid.CreateVersion7(),
            PlayerId = playerId,
            Slot = slot,
            Name = name,
            EquippedJson = await db.AvatarConfigs.Where(a => a.PlayerId == playerId).Select(a => a.EquippedJson).SingleAsync(cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SavedOutfits.Add(outfit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Problem.Of(409, "outfit_limit_reached");
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/me/outfits/{outfit.Id}", new OutfitCreated(outfit.Id));
    }

    private static async Task<OutfitResponse> RenameAsync(
        Guid outfitId, OutfitNameRequest request, VybeDbContext db, CurrentPlayer current, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var name = ValidName(request);
        var playerId = await current.GetIdAsync(cancellationToken);
        var outfit = await db.SavedOutfits.SingleOrDefaultAsync(o => o.Id == outfitId && o.PlayerId == playerId, cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        outfit.Name = name;
        outfit.UpdatedAt = clock.UtcNowForDb();
        await db.SaveChangesAsync(cancellationToken);
        return await ToResponseAsync(db, playerId, await BodyPresetAsync(db, playerId, cancellationToken), outfit, cancellationToken);
    }

    private static async Task<IResult> DeleteAsync(Guid outfitId, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var deleted = await db.SavedOutfits.Where(o => o.Id == outfitId && o.PlayerId == playerId).ExecuteDeleteAsync(cancellationToken);
        return deleted == 1 ? Results.NoContent() : throw Problem.Of(404, "not_found");
    }

    /// <summary>All-or-nothing: every item must still be owned, active and compatible, else nothing changes.</summary>
    private static async Task<EquipmentUpdated> ApplyAsync(
        Guid outfitId, OutfitApplyRequest request, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedAvatarRevision is not { } expected || expected < 0)
        {
            throw Problem.ValidationFailed("/expectedAvatarRevision", "required", "expectedAvatarRevision is required");
        }

        var playerId = await current.GetIdAsync(cancellationToken);
        var outfit = await db.SavedOutfits.AsNoTracking().SingleOrDefaultAsync(o => o.Id == outfitId && o.PlayerId == playerId, cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        var avatar = await db.AvatarConfigs.SingleAsync(a => a.PlayerId == playerId, cancellationToken);
        if (avatar.Revision != expected)
        {
            throw Problem.Of(409, "revision_conflict");
        }

        var equipped = Equipped.FromJson(outfit.EquippedJson);
        var invalid = await InvalidItemsAsync(db, playerId, avatar.BodyPresetId, equipped, cancellationToken);
        if (invalid.Count > 0)
        {
            throw Problem.WithItems(422, "outfit_items_invalid", invalid);
        }

        avatar.EquippedJson = equipped.ToJson();
        await IdentityEndpoints.SaveWithRevisionCheckAsync(db, cancellationToken); // revision compare-and-swap
        return new EquipmentUpdated(avatar.Revision, equipped);
    }

    private static string ValidName(OutfitNameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Name is { Length: >= 1 and <= 40 } name
            ? name
            : throw Problem.ValidationFailed("/name", "invalid", "name must be 1 to 40 characters");
    }

    private static async Task<string> BodyPresetAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken) =>
        await db.AvatarConfigs.Where(a => a.PlayerId == playerId).Select(a => a.BodyPresetId).SingleAsync(cancellationToken);

    private static async Task<OutfitResponse> ToResponseAsync(
        VybeDbContext db, Guid playerId, string bodyPreset, SavedOutfit outfit, CancellationToken cancellationToken)
    {
        var equipped = Equipped.FromJson(outfit.EquippedJson);
        var invalid = await InvalidItemsAsync(db, playerId, bodyPreset, equipped, cancellationToken);
        return new OutfitResponse(outfit.Id, outfit.Name, equipped, invalid.Count == 0, invalid);
    }

    /// <summary>Items that are no longer owned, not active, in another slot or not compatible with the current body preset.</summary>
    private static async Task<List<Guid>> InvalidItemsAsync(
        VybeDbContext db, Guid playerId, string bodyPreset, Equipped equipped, CancellationToken cancellationToken)
    {
        var slots = new List<(ItemSlot Slot, Guid Id)>();
        foreach (var (slot, id) in new[]
        {
            (ItemSlot.Hair, equipped.Hair), (ItemSlot.Top, equipped.Top), (ItemSlot.Bottom, equipped.Bottom),
            (ItemSlot.Shoes, equipped.Shoes), (ItemSlot.Emote, equipped.Emote),
        }.Concat(equipped.Accessories.Select(a => (ItemSlot.Accessory, a))))
        {
            if (id is { } itemId)
            {
                slots.Add((slot, itemId));
            }
        }

        var ids = slots.Select(s => s.Id).Distinct().ToList();
        var usable = await db.PlayerInventory.AsNoTracking()
            .Where(p => p.PlayerId == playerId && ids.Contains(p.ItemId))
            .Join(db.CatalogItems, p => p.ItemId, i => i.Id, (p, i) => i)
            .Where(i => i.IsActive && i.CompatibleBodyPresets.Contains(bodyPreset))
            .Select(i => new { i.Id, i.Slot })
            .ToListAsync(cancellationToken);
        return slots.Where(s => !usable.Any(u => u.Id == s.Id && u.Slot == s.Slot)).Select(s => s.Id).Distinct().ToList();
    }
}
