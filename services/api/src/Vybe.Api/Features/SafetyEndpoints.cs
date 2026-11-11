using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record BlockRequest(string? Handle);

public sealed record BlockedPlayer(string Handle, string? DisplayName, DateTime BlockedAt);

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
        api.MapGet("/me/blocks", ListBlocksAsync);
        api.MapPost("/reports", ReportAsync);
    }

    /// <summary>
    /// Invariant 8: the block row and the cancellation of the pair's <c>open</c>/<c>accepted</c> challenges
    /// commit in one transaction, under the pair lock (invariant 12), so no challenge create, accept or
    /// recipient session that holds the lock can leave a live challenge behind.
    /// </summary>
    private static async Task<IResult> BlockAsync(
        BlockRequest request, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var playerId = await current.GetIdAsync(cancellationToken);
        if (!PlayerHandles.IsInput(request.Handle))
        {
            throw Problem.ValidationFailed("/handle", "invalid", "handle is not a valid handle");
        }

        var target = await PlayerHandles.FindAsync(db, request.Handle, cancellationToken) ?? throw Problem.Of(404, "not_found");
        if (target.Id == playerId)
        {
            throw Problem.Of(422, "self_block");
        }

        var now = clock.UtcNowForDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PairLock.AcquireAsync(db, playerId, target.Id, cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.player_blocks (blocker_player_id, blocked_player_id, created_at)
            VALUES ({playerId}, {target.Id}, {now})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        await db.Challenges
            .Where(c => ((c.CreatorPlayerId == playerId && c.RecipientPlayerId == target.Id)
                    || (c.CreatorPlayerId == target.Id && c.RecipientPlayerId == playerId))
                && (c.Status == ChallengeStatus.Open || c.Status == ChallengeStatus.Accepted))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ChallengeStatus.Cancelled), cancellationToken);
        // P1 friends: the block ends the friendship and any request of the pair, in the same transaction.
        await db.Friendships
            .Where(f => (f.RequesterPlayerId == playerId && f.AddresseePlayerId == target.Id)
                || (f.RequesterPlayerId == target.Id && f.AddresseePlayerId == playerId))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Idempotent; an unknown handle is "not present". Cancelled challenges stay cancelled.</summary>
    private static async Task<IResult> UnblockAsync(
        string handle, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        if (PlayerHandles.IsInput(handle))
        {
            var normalized = handle.ToLowerInvariant();
            await db.PlayerBlocks
                .Where(b => b.BlockerPlayerId == playerId
                    && db.Players.Any(p => p.Id == b.BlockedPlayerId && p.HandleNormalized == normalized))
                .ExecuteDeleteAsync(cancellationToken);
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
        if (Cursor.Decode(cursor) is { } decoded)
        {
            var parts = decoded.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTime.MaxValue.Ticks || !Guid.TryParse(parts[1], out var lastId))
            {
                throw Problem.ValidationFailed("/cursor", "invalid_cursor", "cursor is not valid");
            }

            var lastCreated = new DateTime(ticks, DateTimeKind.Utc);
            query = query.Where(b => b.CreatedAt < lastCreated
                || (b.CreatedAt == lastCreated && b.BlockedPlayerId.CompareTo(lastId) < 0));
        }

        var rows = await query
            .Join(db.Players, b => b.BlockedPlayerId, p => p.Id, (b, p) => new { b.BlockedPlayerId, b.CreatedAt, p.Handle, p.DisplayName })
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.BlockedPlayerId)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var next = rows.Count > take
            ? Cursor.Encode($"{page[^1].CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture)}|{page[^1].BlockedPlayerId}")
            : null;
        return new Page<BlockedPlayer>(page.Select(r => new BlockedPlayer(r.Handle, r.DisplayName, r.CreatedAt)).ToList(), next);
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
        if (target.Id == playerId)
        {
            throw Problem.Of(422, "self_report");
        }

        var now = clock.UtcNowForDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The reporter's row lock serializes the count and the insert, so concurrent reports cannot pass the limit.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE", cancellationToken);
        // One open report per reporter and target: a repeat returns it, so one player cannot flood the queue.
        var open = await db.PlayerReports
            .Where(r => r.ReporterPlayerId == playerId && r.TargetPlayerId == target.Id && r.Status == ReportStatus.Open)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (open is { } existing)
        {
            return Results.Accepted(value: new ReportAccepted(existing));
        }

        var since = now.AddDays(-1);
        if (await db.PlayerReports.CountAsync(r => r.ReporterPlayerId == playerId && r.CreatedAt > since, cancellationToken) >= ReportsPerDay)
        {
            throw Problem.Of(429, "rate_limited");
        }

        var report = new PlayerReport
        {
            Id = Guid.CreateVersion7(),
            ReporterPlayerId = playerId,
            TargetPlayerId = target.Id,
            Reason = reason,
            Details = string.IsNullOrWhiteSpace(request.Details) ? null : request.Details,
            Status = ReportStatus.Open,
            CreatedAt = now,
        };
        db.PlayerReports.Add(report);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Accepted(value: new ReportAccepted(report.Id));
    }
}
