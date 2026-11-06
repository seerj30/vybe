using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record PublicAvatar(
    string BodyPresetId, string SkinToneId, string FacePresetId, string HairPresetId, string HairColorId, Equipped Equipped);

public sealed record PublicProfile(string Handle, string? DisplayName, int Level, PublicAvatar Avatar);

/// <summary>Spec §5 <c>GET /profiles/{handle}</c>: public fields only (ADR 0001, public profile fields).</summary>
public static class ProfileEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/profiles/{handle}", GetAsync).AllowAnonymous();
    }

    /// <summary>
    /// Every caller gets the same answer. Blocks do not change it: a 404 for a blocked caller only would
    /// tell that caller about the block (spec §2 block privacy, ADR 0005).
    /// </summary>
    private static async Task<PublicProfile> GetAsync(string handle, HttpContext context, VybeDbContext db, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store"; // contract: never stored by shared caches
        var player = await PlayerHandles.FindAsync(db, handle, cancellationToken) ?? throw Problem.Of(404, "not_found");
        var avatar = await db.AvatarConfigs.AsNoTracking().SingleAsync(a => a.PlayerId == player.Id, cancellationToken);
        return new PublicProfile(player.Handle, player.DisplayName, player.Level, new PublicAvatar(
            avatar.BodyPresetId, avatar.SkinToneId, avatar.FacePresetId, avatar.HairPresetId, avatar.HairColorId,
            Equipped.FromJson(avatar.EquippedJson)));
    }
}

/// <summary>Handle input (contract <c>HandleInput</c>): matched case-insensitively against the stored lowercase handle.</summary>
public static class PlayerHandles
{
    public static bool IsInput(string? handle) =>
        handle is { Length: >= 3 and <= 20 } && handle.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>The player with this handle, or null for an invalid, unknown, deleting or deleted handle.</summary>
    public static async Task<Player?> FindAsync(VybeDbContext db, string? handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsInput(handle))
        {
            return null;
        }

        var normalized = handle!.ToLowerInvariant();
        return await db.Players.AsNoTracking()
            .SingleOrDefaultAsync(
                p => p.HandleNormalized == normalized && p.Status != PlayerStatus.Deleting && p.Status != PlayerStatus.Deleted,
                cancellationToken);
    }
}
