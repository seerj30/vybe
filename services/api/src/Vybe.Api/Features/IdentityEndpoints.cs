using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vybe.Api.Content;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record GuestCreated(Guid PlayerId, string Handle, DateTime GuestExpiresAt);

public sealed record MeResponse(
    Guid Id, string Handle, PlayerKind Kind, string Status, DateTime? SuspendedUntil, int Level, long XpTotal,
    long WalletBalance, long AvatarRevision);

public sealed record AvatarResponse(
    string BodyPresetId, string SkinToneId, string FacePresetId, string HairPresetId, string HairColorId,
    long Revision, Equipped Equipped);

public sealed record AvatarUpdateRequest(
    string BodyPresetId, string SkinToneId, string FacePresetId, string HairPresetId, string HairColorId, long ExpectedRevision);

public sealed record AvatarUpdated(long Revision, AvatarResponse Avatar);

public static class IdentityEndpoints
{
    private const string HandleAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/guests", CreateGuestAsync).AllowAnonymous().RequireRateLimiting(ServiceSetup.GuestCreationPolicy);
        api.MapGet("/me", GetMeAsync);
        api.MapGet("/me/avatar", GetAvatarAsync);
        api.MapPut("/me/avatar", UpdateAvatarAsync);
    }

    private static async Task<IResult> CreateGuestAsync(
        HttpContext context, VybeDbContext db, CurrentPlayer current, ContentCatalog content, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await current.TryGetIdAsync(cancellationToken) is not null)
        {
            throw Problem.Of(409, "already_authenticated");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var token = GuestTokens.NewToken();
        var expiresAt = now + GuestTokens.Lifetime;
        var starterSkus = content.StarterSkus.ToArray();
        var starterItems = await db.CatalogItems.AsNoTracking()
            .Where(i => starterSkus.Contains(i.Sku))
            .ToListAsync(cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            var player = new Player
            {
                Id = Guid.CreateVersion7(),
                Handle = "guest_" + RandomNumberGenerator.GetString(HandleAlphabet, 10),
                HandleNormalized = string.Empty,
                Kind = PlayerKind.Guest,
                Status = PlayerStatus.Active,
            };
            player.HandleNormalized = player.Handle;

            var equipped = Equipped.Empty;
            foreach (var item in starterItems)
            {
                equipped = equipped.With(item.Slot, item.Id, item.Slot == ItemSlot.Accessory ? 0 : null);
            }

            var defaults = content.Avatar.Defaults;
            db.Players.Add(player);
            db.Wallets.Add(new Wallet { PlayerId = player.Id });
            db.AvatarConfigs.Add(new AvatarConfig
            {
                PlayerId = player.Id,
                BodyPresetId = defaults.BodyPresetId,
                SkinToneId = defaults.SkinToneId,
                FacePresetId = defaults.FacePresetId,
                HairPresetId = defaults.HairPresetId,
                HairColorId = defaults.HairColorId,
                EquippedJson = equipped.ToJson(),
            });
            db.PlayerInventory.AddRange(starterItems.Select(i => new PlayerInventoryItem
            {
                PlayerId = player.Id,
                ItemId = i.Id,
                Source = InventorySource.StarterGrant,
            }));
            db.GuestSessions.Add(new GuestSession { PlayerId = player.Id, TokenHash = GuestTokens.Hash(token), ExpiresAt = expiresAt });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                GuestTokens.SetCookie(context.Response, token, expiresAt);
                return TypedResults.Created("/api/v1/me", new GuestCreated(player.Id, player.Handle, expiresAt));
            }
            catch (DbUpdateException e) when (attempt < 5 && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear(); // random handle collision: try another
            }
        }
    }

    private static async Task<MeResponse> GetMeAsync(
        VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        var id = await current.GetIdAsync(cancellationToken);
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == id, cancellationToken);
        var balance = await db.Wallets.Where(w => w.PlayerId == id).Select(w => w.Balance).SingleAsync(cancellationToken);
        var revision = await db.AvatarConfigs.Where(a => a.PlayerId == id).Select(a => a.Revision).SingleAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var suspended = CurrentPlayer.IsSuspended(player, now);
        var status = player.Status switch
        {
            PlayerStatus.Deleting => "deleting",
            PlayerStatus.Suspended when suspended => "suspended",
            _ => "active",
        };

        return new MeResponse(player.Id, player.Handle, player.Kind, status, suspended ? player.SuspendedUntil : null,
            player.Level, player.XpTotal, balance, revision);
    }

    private static async Task<AvatarResponse> GetAvatarAsync(VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var id = await current.GetIdAsync(cancellationToken);
        return ToResponse(await db.AvatarConfigs.AsNoTracking().SingleAsync(a => a.PlayerId == id, cancellationToken));
    }

    private static async Task<AvatarUpdated> UpdateAvatarAsync(
        AvatarUpdateRequest request, VybeDbContext db, CurrentPlayer current, ContentCatalog content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = await current.GetIdAsync(cancellationToken);
        if (!content.IsValidPresetSet(request.BodyPresetId, request.SkinToneId, request.FacePresetId, request.HairPresetId, request.HairColorId))
        {
            throw Problem.Of(422, "invalid_preset");
        }

        var avatar = await db.AvatarConfigs.SingleAsync(a => a.PlayerId == id, cancellationToken);
        if (avatar.Revision != request.ExpectedRevision)
        {
            throw Problem.Of(409, "revision_conflict");
        }

        if (request.BodyPresetId != avatar.BodyPresetId)
        {
            var equippedIds = Equipped.FromJson(avatar.EquippedJson).ItemIds().ToList();
            var incompatible = await db.CatalogItems.AsNoTracking()
                .Where(i => equippedIds.Contains(i.Id) && !i.CompatibleBodyPresets.Contains(request.BodyPresetId))
                .Select(i => i.Id)
                .ToListAsync(cancellationToken);
            if (incompatible.Count > 0)
            {
                throw Problem.WithItems(409, "incompatible_equipment", incompatible);
            }
        }

        avatar.BodyPresetId = request.BodyPresetId;
        avatar.SkinToneId = request.SkinToneId;
        avatar.FacePresetId = request.FacePresetId;
        avatar.HairPresetId = request.HairPresetId;
        avatar.HairColorId = request.HairColorId;
        await SaveWithRevisionCheckAsync(db, cancellationToken);
        var response = ToResponse(avatar);
        return new AvatarUpdated(response.Revision, response);
    }

    internal static AvatarResponse ToResponse(AvatarConfig avatar) =>
        new(avatar.BodyPresetId, avatar.SkinToneId, avatar.FacePresetId, avatar.HairPresetId, avatar.HairColorId,
            avatar.Revision, Equipped.FromJson(avatar.EquippedJson));

    internal static async Task SaveWithRevisionCheckAsync(VybeDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Problem.Of(409, "revision_conflict");
        }
    }
}
