using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

/// <summary>
/// The plaza hub (protocol doc §4). Thin: every rule lives in <see cref="SpaceManager"/>. The only arguments are
/// integers and ids from fixed lists; no method takes free text.
/// </summary>
public sealed class SpaceHub(SpaceManager spaces, VybeDbContext db, TimeProvider clock) : Hub
{
    public const string Path = "/api/v1/spaces/hub";

    /// <summary>Profile reads (5 small queries) at the same time, for all connections: joins cannot flood the pool.</summary>
    private static readonly SemaphoreSlim ProfileReads = new(8);

    /// <summary>
    /// <c>Report</c> and <c>Block</c> writes at the same time, for all connections: each is a transaction with row
    /// locks, so many connections cannot take the database pool from the HTTP API.
    /// </summary>
    private static readonly SemaphoreSlim SafetyWrites = new(8);

    public override Task OnConnectedAsync()
    {
        var playerId = Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var address = Context.GetHttpContext()?.Items[SpaceGate.AddressItem] as string ?? "unknown";
        spaces.Connected(Context, playerId, address);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        spaces.Disconnected(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public async Task<SpaceWelcome> Join()
    {
        var playerId = spaces.BeginJoin(Context.ConnectionId);
        SpaceProfile? profile;
        try
        {
            profile = await LoadProfileAsync(playerId);
        }
        catch
        {
            spaces.CancelJoin(Context.ConnectionId);
            throw;
        }

        if (profile is null)
        {
            spaces.CancelJoin(Context.ConnectionId);
            spaces.Kick(Context.ConnectionId, KickReasons.AccountUnavailable);
            throw new HubException(KickReasons.AccountUnavailable);
        }

        return spaces.CompleteJoin(Context.ConnectionId, profile);
    }

    public void Leave() => spaces.Leave(Context.ConnectionId);

    public void Move(int dx, int dz) => spaces.Move(Context.ConnectionId, dx, dz);

    public void Emote(string? sku) => spaces.Emote(Context.ConnectionId, sku);

    public void QuickChat(string? id) => spaces.QuickChat(Context.ConnectionId, id);

    public void Sticker(string? id) => spaces.Sticker(Context.ConnectionId, id);

    /// <summary>
    /// Blocks the member in <paramref name="slot"/> (protocol doc §9), also a player with a private profile: the same
    /// block as <c>POST /me/blocks</c>. The server then moves the caller to another instance.
    /// </summary>
    public async Task Block(int slot, int key)
    {
        var (caller, target) = spaces.SafetyTarget(Context.ConnectionId, "block", slot, key);
        await SafetyWriteAsync(() => SafetyEndpoints.BlockPlayerAsync(db, spaces, caller, target, clock.UtcNowForDb(), Context.ConnectionAborted));
    }

    /// <summary>
    /// Reports the member in <paramref name="slot"/> with a fixed reason (no free text): the same report as
    /// <c>POST /reports</c>. A repeat while the report is open is accepted again; over the daily limit: <c>rate_limited</c>.
    /// </summary>
    public async Task Report(int slot, int key, string? reason)
    {
        if (!ReportReasons.TryGetValue(reason ?? "", out var parsed))
        {
            spaces.InvalidInput(Context.ConnectionId);
            throw new HubException(KickReasons.InvalidInput);
        }

        var (caller, target) = spaces.SafetyTarget(Context.ConnectionId, "report", slot, key);
        Guid? report = null;
        await SafetyWriteAsync(async () =>
            report = await SafetyEndpoints.ReportPlayerAsync(db, caller, target, parsed, null, clock.UtcNowForDb(), Context.ConnectionAborted));
        _ = report ?? throw new HubException(KickReasons.RateLimited);
    }

    /// <summary>Runs a safety write inside <see cref="SafetyWrites"/>; API problems become hub error codes.</summary>
    private async Task SafetyWriteAsync(Func<Task> write)
    {
        if (!await SafetyWrites.WaitAsync(TimeSpan.FromSeconds(2), Context.ConnectionAborted))
        {
            throw new HubException(KickReasons.RateLimited);
        }

        try
        {
            await write();
        }
        catch (ApiProblemException problem) when (problem.Status is 404 or 422)
        {
            throw new HubException(SafetyErrors.NotFound); // the member was deleted meanwhile (or is the caller)
        }
        finally
        {
            SafetyWrites.Release();
        }
    }

    /// <summary>The <c>ReportReason</c> names of the HTTP API.</summary>
    private static readonly Dictionary<string, ReportReason> ReportReasons = new(StringComparer.Ordinal)
    {
        ["offensive_name"] = ReportReason.OffensiveName,
        ["cheating"] = ReportReason.Cheating,
        ["harassment"] = ReportReason.Harassment,
        ["other"] = ReportReason.Other,
    };

    private async Task<SpaceProfile?> LoadProfileAsync(Guid playerId)
    {
        if (!await ProfileReads.WaitAsync(TimeSpan.FromSeconds(5), Context.ConnectionAborted))
        {
            throw new HubException(KickReasons.RateLimited);
        }

        try
        {
            return await SpaceProfiles.LoadAsync(db, playerId, clock.UtcNowForDb(), Context.ConnectionAborted);
        }
        finally
        {
            ProfileReads.Release();
        }
    }

    public async Task RefreshLook()
    {
        if (spaces.BeginRefreshLook(Context.ConnectionId) is { } playerId
            && await LoadProfileAsync(playerId) is { } profile)
        {
            spaces.CompleteRefreshLook(Context.ConnectionId, profile);
        }
    }
}

/// <summary>Reads what a space needs about a player: public handle, look (item SKUs), owned emotes, blocks.</summary>
public static class SpaceProfiles
{
    /// <summary>The profile, or null for a missing, suspended, <c>deleting</c> or deleted player.</summary>
    public static async Task<SpaceProfile?> LoadAsync(VybeDbContext db, Guid playerId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var player = await db.Players.AsNoTracking()
            .Where(p => p.Id == playerId)
            .Select(p => new { p.Handle, p.ProfileVisibility, p.Status, p.SuspendedUntil })
            .SingleOrDefaultAsync(cancellationToken);
        if (player is null || player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted
            || (player.Status == PlayerStatus.Suspended && (player.SuspendedUntil is null || player.SuspendedUntil > now)))
        {
            return null;
        }

        var avatar = await db.AvatarConfigs.AsNoTracking().SingleAsync(a => a.PlayerId == playerId, cancellationToken);
        var equipped = Equipped.FromJson(avatar.EquippedJson);
        var ids = equipped.ItemIds().ToList();
        var skus = await db.CatalogItems.AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Sku, cancellationToken);
        var emotes = await db.PlayerInventory.AsNoTracking()
            .Where(i => i.PlayerId == playerId)
            .Join(db.CatalogItems.Where(c => c.Slot == ItemSlot.Emote), i => i.ItemId, c => c.Id, (_, c) => c.Sku)
            .ToListAsync(cancellationToken);
        var blocks = await db.PlayerBlocks.AsNoTracking()
            .Where(b => b.BlockerPlayerId == playerId || b.BlockedPlayerId == playerId)
            .Select(b => new { b.BlockerPlayerId, b.BlockedPlayerId })
            .ToListAsync(cancellationToken);

        string? Sku(Guid? id) => id is { } value ? skus.GetValueOrDefault(value) : null;
        var look = new SpaceLook(
            avatar.BodyPresetId, avatar.SkinToneId, avatar.FacePresetId, avatar.HairPresetId, avatar.HairColorId,
            new SpaceEquipped(
                Sku(equipped.Hair), Sku(equipped.Top), Sku(equipped.Bottom), Sku(equipped.Shoes), Sku(equipped.Emote),
                [.. equipped.Accessories.Select(Sku)]));
        return new SpaceProfile(
            player.ProfileVisibility == ProfileVisibility.Private ? null : player.Handle,
            look,
            emotes.ToHashSet(StringComparer.Ordinal),
            blocks.Select(b => b.BlockerPlayerId == playerId ? b.BlockedPlayerId : b.BlockerPlayerId).ToHashSet(),
            blocks.Where(b => b.BlockedPlayerId == playerId).Select(b => b.BlockerPlayerId).ToHashSet());
    }
}
