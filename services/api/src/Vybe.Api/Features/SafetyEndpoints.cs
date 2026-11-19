using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record BlockRequest(string? Handle);

/// <summary>
/// A block of the caller. <c>Handle</c> and <c>DisplayName</c> are null when the blocked player's profile is private
/// (decision 6): a block from the plaza must not show who it was. <c>Ref</c> unblocks without a handle.
/// </summary>
public sealed record BlockedPlayer(string? Handle, string? DisplayName, DateTime BlockedAt, string Ref);

public sealed record ReportRequest(string? TargetHandle, ReportReason? Reason, string? Details);

public sealed record ReportAccepted(Guid ReportId);

/// <summary>Spec §5 Safety: blocks (invariants 8 and 12, block privacy) and reports.</summary>
public static class SafetyEndpoints
{
    /// <summary>Provisional (spec §5): reports per reporter in a rolling 24 hours.</summary>
    public const int ReportsPerDay = 10;

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/me/blocks", BlockAsync);
        api.MapDelete("/me/blocks/{handle}", UnblockAsync);
        api.MapDelete("/me/blocks/ref/{reference}", UnblockByRefAsync);
        api.MapGet("/me/blocks", ListBlocksAsync);
        api.MapPost("/reports", ReportAsync);
    }

    /// <summary>
    /// Invariant 8: the block row and the cancellation of the pair's <c>open</c>/<c>accepted</c> challenges
    /// commit in one transaction, under the pair lock (invariant 12), so no challenge create, accept or
    /// recipient session that holds the lock can leave a live challenge behind.
    /// </summary>
    private static async Task<IResult> BlockAsync(
        BlockRequest request, VybeDbContext db, CurrentPlayer current, TimeProvider clock, SpaceManager spaces,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var playerId = await current.GetIdAsync(cancellationToken);
        if (!PlayerHandles.IsInput(request.Handle))
        {
            throw Problem.ValidationFailed("/handle", "invalid", "handle is not a valid handle");
        }

        // Decision 6 (accepted risk): a private player stays blockable by handle.
        var target = await PlayerHandles.FindAsync(db, request.Handle, cancellationToken) ?? throw Problem.Of(404, "not_found");

        await BlockPlayerAsync(db, spaces, playerId, target.Id, clock.UtcNowForDb(), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// The block itself, for <c>POST /me/blocks</c> and the plaza hub (<c>Block(slot, key)</c>). Idempotent.
    /// </summary>
    internal static async Task BlockPlayerAsync(
        VybeDbContext db, SpaceManager spaces, Guid playerId, Guid targetId, DateTime now, CancellationToken cancellationToken)
    {
        if (targetId == playerId)
        {
            throw Problem.Of(422, "self_block");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PairLock.AcquireAsync(db, playerId, targetId, cancellationToken);
        // A deletion that started after the lookup (J4) must not leave a block row behind (404 like an unknown handle).
        await FriendEndpoints.EnsureStillListedAsync(db, targetId, cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.player_blocks (blocker_player_id, blocked_player_id, created_at)
            VALUES ({playerId}, {targetId}, {now})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        await db.Challenges
            .Where(c => ((c.CreatorPlayerId == playerId && c.RecipientPlayerId == targetId)
                    || (c.CreatorPlayerId == targetId && c.RecipientPlayerId == playerId))
                && (c.Status == ChallengeStatus.Open || c.Status == ChallengeStatus.Accepted))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ChallengeStatus.Cancelled), cancellationToken);
        await FriendEndpoints.ApplyBlockAsync(db, playerId, targetId, cancellationToken);
        // Not cancelled once here: a commit must be followed by the live block in the spaces.
        await transaction.CommitAsync(CancellationToken.None);
        spaces.ApplyBlock(playerId, targetId); // real-time spaces: the pair never shares an instance (P2)
    }

    /// <summary>Idempotent; an unknown handle is "not present". Cancelled challenges stay cancelled.</summary>
    private static async Task<IResult> UnblockAsync(
        string handle, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        if (PlayerHandles.IsInput(handle))
        {
            // A private player's block is not matched by handle (decision 6; unblock it by ref), so the answer and the
            // list never confirm a guessed handle.
            var normalized = handle.ToLowerInvariant();
            await db.PlayerBlocks
                .Where(b => b.BlockerPlayerId == playerId
                    && db.Players.Any(p => p.Id == b.BlockedPlayerId && p.HandleNormalized == normalized
                        && p.ProfileVisibility != ProfileVisibility.Private))
                .ExecuteDeleteAsync(cancellationToken);
            await FriendEndpoints.ApplyUnblockAsync(db, playerId, handle, cancellationToken);
        }

        return Results.NoContent();
    }

    /// <summary>Only the caller's own blocks, newest first; never who blocked the caller.</summary>
    private static async Task<Page<BlockedPlayer>> ListBlocksAsync(
        string? cursor, int? limit, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var take = Cursor.Limit(limit, 20);
        var query = db.PlayerBlocks.AsNoTracking().Where(b => b.BlockerPlayerId == playerId);
        DateTime? lastCreated = null;
        var skip = 0;
        if (Cursor.Decode(cursor) is { } decoded)
        {
            // "ticks|n": the last time on the previous page and how many of its blocks had that time. No player id:
            // the cursor of a private player's block must not identify the player.
            var parts = decoded.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTime.MaxValue.Ticks
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seen) || seen < 1)
            {
                throw Problem.ValidationFailed("/cursor", "invalid_cursor", "cursor is not valid");
            }

            lastCreated = new DateTime(ticks, DateTimeKind.Utc);
            skip = seen;
            query = query.Where(b => b.CreatedAt <= lastCreated);
        }

        var rows = await query
            .Join(db.Players, b => b.BlockedPlayerId, p => p.Id, (b, p) => new { b.BlockedPlayerId, b.CreatedAt, p.Handle, p.DisplayName, p.ProfileVisibility })
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.BlockedPlayerId)
            .Skip(skip) // the blocks with the cursor's time that the previous pages showed come first
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        string? next = null;
        if (rows.Count > take)
        {
            var last = page[^1].CreatedAt;
            var seen = page.Count(r => r.CreatedAt == last) + (lastCreated == last ? skip : 0);
            next = Cursor.Encode($"{last.Ticks.ToString(CultureInfo.InvariantCulture)}|{seen.ToString(CultureInfo.InvariantCulture)}");
        }

        return new Page<BlockedPlayer>(
            page.Select(r => r.ProfileVisibility == ProfileVisibility.Private
                    ? new BlockedPlayer(null, null, r.CreatedAt, BlockRef(r.CreatedAt))
                    : new BlockedPlayer(r.Handle, r.DisplayName, r.CreatedAt, BlockRef(r.CreatedAt)))
                .ToList(),
            next);
    }

    /// <summary>An opaque reference to one of the caller's blocks: its time (a caller blocks one player at a time).</summary>
    private static string BlockRef(DateTime createdAt) => Cursor.Encode(createdAt.Ticks.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Unblock by <see cref="BlockedPlayer.Ref"/>, for a block whose handle is not shown. Idempotent; a reference
    /// matches only the caller's own blocks.
    /// </summary>
    private static async Task<IResult> UnblockByRefAsync(
        string reference, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        if (Cursor.Decode(reference) is not { } decoded
            || !long.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks)
        {
            throw Problem.ValidationFailed("/reference", "invalid", "reference is not valid");
        }

        var createdAt = new DateTime(ticks, DateTimeKind.Utc);
        var blocked = await db.PlayerBlocks
            .Where(b => b.BlockerPlayerId == playerId && b.CreatedAt == createdAt)
            .Select(b => b.BlockedPlayerId)
            .ToListAsync(cancellationToken);
        foreach (var otherId in blocked)
        {
            await db.PlayerBlocks.Where(b => b.BlockerPlayerId == playerId && b.BlockedPlayerId == otherId).ExecuteDeleteAsync(cancellationToken);
            await FriendEndpoints.ApplyUnblockAsync(db, playerId, otherId, cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ReportAsync(
        ReportRequest request, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var playerId = await current.GetIdAsync(cancellationToken);
        if (!PlayerHandles.IsInput(request.TargetHandle))
        {
            throw Problem.ValidationFailed("/targetHandle", "invalid", "targetHandle is not a valid handle");
        }

        var reason = request.Reason ?? throw Problem.ValidationFailed("/reason", "required", "reason is required");
        if (request.Details is { Length: > 500 })
        {
            throw Problem.ValidationFailed("/details", "too_long", "details must be at most 500 characters");
        }

        var target = await PlayerHandles.FindAsync(db, request.TargetHandle, cancellationToken) ?? throw Problem.Of(404, "not_found");

        var accepted = await ReportPlayerAsync(db, playerId, target.Id, reason, request.Details, clock.UtcNowForDb(), cancellationToken)
            ?? throw Problem.Of(429, "rate_limited");
        return Results.Accepted(value: new ReportAccepted(accepted));
    }

    /// <summary>
    /// The report itself, for <c>POST /reports</c> and the plaza hub (<c>Report(slot, key, reason)</c>): the id of the
    /// new or the still open report of this pair, or null when the reporter is over <see cref="ReportsPerDay"/>.
    /// </summary>
    internal static async Task<Guid?> ReportPlayerAsync(
        VybeDbContext db, Guid playerId, Guid targetId, ReportReason reason, string? details, DateTime now,
        CancellationToken cancellationToken)
    {
        if (targetId == playerId)
        {
            throw Problem.Of(422, "self_report");
        }

        // A plain read, no row lock: a share lock on the target here and the reporter's row lock below would deadlock
        // when two players report each other. A report that races a deletion keeps only pseudonymous ids.
        if (!await db.Players.AnyAsync(
                p => p.Id == targetId && p.Status != PlayerStatus.Deleting && p.Status != PlayerStatus.Deleted, cancellationToken))
        {
            throw Problem.Of(404, "not_found");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The reporter's row lock serializes the count and the insert, so concurrent reports cannot pass the limit.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE", cancellationToken);
        // One open report per reporter and target: a repeat returns it, so one player cannot flood the queue.
        var open = await db.PlayerReports
            .Where(r => r.ReporterPlayerId == playerId && r.TargetPlayerId == targetId && r.Status == ReportStatus.Open)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (open is { } existing)
        {
            return existing;
        }

        var since = now.AddDays(-1);
        if (await db.PlayerReports.CountAsync(r => r.ReporterPlayerId == playerId && r.CreatedAt > since, cancellationToken) >= ReportsPerDay)
        {
            return null;
        }

        var report = new PlayerReport
        {
            Id = Guid.CreateVersion7(),
            ReporterPlayerId = playerId,
            TargetPlayerId = targetId,
            Reason = reason,
            Details = string.IsNullOrWhiteSpace(details) ? null : details,
            Status = ReportStatus.Open,
            CreatedAt = now,
        };
        db.PlayerReports.Add(report);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return report.Id;
    }
}
