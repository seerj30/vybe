using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record PublicAvatar(
    string BodyPresetId, string SkinToneId, string FacePresetId, string HairPresetId, string HairColorId, Equipped Equipped);

public sealed record PublicProfile(string Handle, string? DisplayName, int Level, PublicAvatar Avatar);

public sealed record PrivacySettingsRequest(ProfileVisibility? ProfileVisibility);

public sealed record PrivacySettings(ProfileVisibility ProfileVisibility);

/// <summary>
/// Spec §5 <c>GET /profiles/{handle}</c>: public fields only (ADR 0001, public profile fields), and the
/// profile privacy setting (spec §17 decision 6).
/// </summary>
public static class ProfileEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        // Anonymous callers are let through, so they get the same 404 as an unknown handle (not a 401).
        api.MapGet("/profiles/{handle}", GetAsync).AllowAnonymous();
        api.MapPatch("/me/privacy", UpdatePrivacyAsync);
    }

    /// <summary>
    /// Signed-in callers only (guest or registered); a private profile only for its owner. Every other case is
    /// the 404 of an unknown handle, so the answer does not tell why. Blocks do not change it: a 404 for a
    /// blocked caller only would tell that caller about the block (spec §2 block privacy, ADR 0005).
    /// </summary>
    private static async Task<IResult> GetAsync(
        string handle, HttpContext context, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        // Written here, not thrown: the exception handler would clear these headers from the 404.
        context.Response.Headers.CacheControl = "private, no-store"; // contract: never stored by shared caches
        context.Response.Headers["X-Robots-Tag"] = "noindex";
        var callerId = await current.TryGetIdAsync(cancellationToken);
        var player = callerId is null ? null : await PlayerHandles.FindAsync(db, handle, cancellationToken);
        if (player is null || (player.ProfileVisibility == ProfileVisibility.Private && player.Id != callerId))
        {
            await ProblemWriter.WriteAsync(context, 404, "not_found");
            return Results.Empty;
        }

        var avatar = await db.AvatarConfigs.AsNoTracking().SingleAsync(a => a.PlayerId == player.Id, cancellationToken);
        return TypedResults.Ok(new PublicProfile(player.Handle, player.DisplayName, player.Level, new PublicAvatar(
            avatar.BodyPresetId, avatar.SkinToneId, avatar.FacePresetId, avatar.HairPresetId, avatar.HairColorId,
            Equipped.FromJson(avatar.EquippedJson))));
    }

    /// <summary><c>PATCH /me/privacy</c>: sets who can see the caller's profile; idempotent.</summary>
    private static async Task<PrivacySettings> UpdatePrivacyAsync(
        HttpContext context, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var request = RequestBody.Deserialize<PrivacySettingsRequest>(await RequestBody.ReadAsync(context, cancellationToken));
        // The enum converter also reads numbers, so an undefined value is possible here.
        if (request.ProfileVisibility is not { } visibility || !Enum.IsDefined(visibility))
        {
            throw Problem.ValidationFailed("/profileVisibility", "invalid", "profileVisibility must be players or private");
        }

        await db.Players.Where(p => p.Id == playerId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ProfileVisibility, visibility).SetProperty(p => p.UpdatedAt, clock.UtcNowForDb()), cancellationToken);
        return new PrivacySettings(visibility);
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
