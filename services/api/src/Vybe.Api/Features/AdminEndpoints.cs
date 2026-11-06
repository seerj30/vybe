using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record PlayerRef(Guid PlayerId, string Handle);

public sealed record AdminReport(
    Guid ReportId, PlayerRef Reporter, PlayerRef Target, ReportReason Reason, string? Details, ReportStatus Status, DateTime CreatedAt,
    DateTime? ReviewedAt);

public sealed record ReportResolveRequest(ReportStatus? Status, string? Reason);

public sealed record AdminReasonRequest(string? Reason);

public sealed record SuspensionRequest(string? Reason, DateTime? Until);

public sealed record Suspension(Guid PlayerId, bool Suspended, DateTime? SuspendedUntil);

public sealed record CatalogItemUpdateRequest(bool? IsActive, string? Reason);

public sealed record AdminCatalogItem(Guid Id, string Sku, string Name, bool IsActive);

public sealed record WalletAdjustmentRequest(Guid? PlayerId, long? Delta, string? Reason);

public sealed record WalletAdjustment(Guid TransactionId, Guid PlayerId, long Delta, long BalanceAfter, Guid AuditLogId);

public sealed record AuditLogEntry(
    Guid Id, string ActorSubject, AdminRole ActorRole, string Action, string EntityType, string EntityId, JsonElement? Before,
    JsonElement? After, string Reason, DateTime CreatedAt);

/// <summary>
/// Spec §5 Admin (base <c>/api/v1/admin</c>, bearer token only). Every write checks the reason and writes
/// <c>admin_audit_logs</c> in its own transaction (ADR 0005).
/// </summary>
public static class AdminEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var moderator = api.MapGroup("/admin").RequireAuthorization(p => AdminAccess.Require(p, AdminRole.Moderator));
        moderator.MapGet("/reports", ListReportsAsync);
        moderator.MapPost("/reports/{reportId:guid}/resolve", ResolveReportAsync);
        moderator.MapPost("/players/{playerId:guid}/suspension", SuspendAsync);
        moderator.MapPost("/players/{playerId:guid}/suspension/lift", LiftSuspensionAsync);

        var administrator = api.MapGroup("/admin").RequireAuthorization(p => AdminAccess.Require(p, AdminRole.Administrator));
        administrator.MapPatch("/catalog/items/{itemId:guid}", UpdateCatalogItemAsync);
        administrator.MapPost("/wallet-adjustments", AdjustWalletAsync);
        administrator.MapGet("/audit-logs", ListAuditLogsAsync);
    }

    /// <summary>The review queue: oldest first, so reports are handled in order.</summary>
    private static async Task<Page<AdminReport>> ListReportsAsync(
        string? status, string? cursor, int? limit, VybeDbContext db, CancellationToken cancellationToken)
    {
        var take = Cursor.Limit(limit, 20);
        var query = db.PlayerReports.AsNoTracking();
        if (status is not null)
        {
            var parsed = TextEnums.Values<ReportStatus>().Contains(status)
                ? TextEnums.FromDatabase<ReportStatus>(status)
                : throw Problem.ValidationFailed("/status", "invalid", "unknown report status");
            query = query.Where(r => r.Status == parsed);
        }

        if (Cursor.Decode(cursor) is { } decoded)
        {
            var (created, id) = ParseTimeCursor(decoded);
            query = query.Where(r => r.CreatedAt > created || (r.CreatedAt == created && r.Id.CompareTo(id) > 0));
        }

        var rows = await query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var players = await HandlesAsync(db, page.SelectMany(r => new[] { r.ReporterPlayerId, r.TargetPlayerId }), cancellationToken);
        var items = page.Select(r => ToResponse(r, players)).ToList();
        return new Page<AdminReport>(items, rows.Count > take ? TimeCursor(page[^1].CreatedAt, page[^1].Id) : null);
    }

    private static async Task<AdminReport> ResolveReportAsync(
        Guid reportId, ReportResolveRequest request, HttpContext context, VybeDbContext db, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = AdminAccess.Actor(context.User);
        if (request.Status is not (ReportStatus.Actioned or ReportStatus.Dismissed))
        {
            throw Problem.ValidationFailed("/status", "invalid", "status must be actioned or dismissed");
        }

        var reason = AdminAccess.ValidReason(request.Reason);
        var now = clock.UtcNowForDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var report = await db.PlayerReports.FromSql($"SELECT * FROM vybe.player_reports WHERE id = {reportId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw Problem.Of(404, "not_found");
        var before = new { report.Status, report.ReviewedAt, report.ReviewedBy };
        report.Status = request.Status.Value;
        report.ReviewedAt = now;
        report.ReviewedBy = actor.Subject;
        AdminAccess.Audit(db, actor, "report.resolve", "player_report", report.Id.ToString(), before,
            new { report.Status, report.ReviewedAt, report.ReviewedBy }, reason, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var players = await HandlesAsync(db, [report.ReporterPlayerId, report.TargetPlayerId], cancellationToken);
        return ToResponse(report, players);
    }

    private static async Task<Suspension> SuspendAsync(
        Guid playerId, SuspensionRequest request, HttpContext context, VybeDbContext db, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = AdminAccess.Actor(context.User);
        var reason = AdminAccess.ValidReason(request.Reason);
        var now = clock.UtcNowForDb();
        var until = request.Until?.ToUniversalTime();
        if (until <= now)
        {
            throw Problem.ValidationFailed("/until", "invalid", "until must be in the future");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var player = await LockPlayerAsync(db, playerId, cancellationToken);
        var before = SuspensionState(player);
        player.Status = PlayerStatus.Suspended;
        player.SuspendedUntil = until;
        player.SuspensionReason = reason;
        player.UpdatedAt = now;
        AdminAccess.Audit(db, actor, "player.suspend", "player", playerId.ToString(), before, SuspensionState(player), reason, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Suspension(playerId, true, until);
    }

    private static async Task<Suspension> LiftSuspensionAsync(
        Guid playerId, AdminReasonRequest request, HttpContext context, VybeDbContext db, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = AdminAccess.Actor(context.User);
        var reason = AdminAccess.ValidReason(request.Reason);
        var now = clock.UtcNowForDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var player = await LockPlayerAsync(db, playerId, cancellationToken);
        var before = SuspensionState(player);
        if (player.Status == PlayerStatus.Suspended)
        {
            player.Status = PlayerStatus.Active;
            player.SuspendedUntil = null;
            player.SuspensionReason = null;
            player.UpdatedAt = now;
        }

        AdminAccess.Audit(db, actor, "player.lift_suspension", "player", playerId.ToString(), before, SuspensionState(player), reason, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Suspension(playerId, false, null);
    }

    private static async Task<AdminCatalogItem> UpdateCatalogItemAsync(
        Guid itemId, CatalogItemUpdateRequest request, HttpContext context, VybeDbContext db, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = AdminAccess.Actor(context.User);
        var isActive = request.IsActive ?? throw Problem.ValidationFailed("/isActive", "required", "isActive is required");
        var reason = AdminAccess.ValidReason(request.Reason);
        var now = clock.UtcNowForDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.CatalogItems.FromSql($"SELECT * FROM vybe.catalog_items WHERE id = {itemId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw Problem.Of(404, "not_found");
        var before = new { item.IsActive };
        item.IsActive = isActive;
        AdminAccess.Audit(db, actor, "catalog_item.update", "catalog_item", item.Id.ToString(), before, new { item.IsActive }, reason, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminCatalogItem(item.Id, item.Sku, item.Name, item.IsActive);
    }

    /// <summary>
    /// Ledger key <c>admin:{Idempotency-Key}</c> (spec §4): with the idempotency record and the unique ledger
    /// key, a retry never adjusts twice. The audit row is the ledger row's reference.
    /// </summary>
    private static async Task<IResult> AdjustWalletAsync(
        HttpContext context, VybeDbContext db, Idempotency idempotency, TimeProvider clock, CancellationToken cancellationToken)
    {
        var actor = AdminAccess.Actor(context.User);
        var key = Idempotency.ReadKey(context.Request);
        var body = await RequestBody.ReadAsync(context, cancellationToken);
        var request = RequestBody.Deserialize<WalletAdjustmentRequest>(body);
        var playerId = request.PlayerId ?? throw Problem.ValidationFailed("/playerId", "required", "playerId is required");
        var delta = request.Delta is { } d and not 0 ? d : throw Problem.ValidationFailed("/delta", "invalid", "delta must be a non-zero integer");
        var reason = AdminAccess.ValidReason(request.Reason);
        var hash = Idempotency.Hash("admin.wallet_adjustment", string.Empty, body);

        return await idempotency.RunAsync(context, actor.Subject, "admin.wallet_adjustment", hash, async ct =>
        {
            var exists = await db.Players.AnyAsync(p => p.Id == playerId && p.Status != PlayerStatus.Deleted, ct);
            if (!exists)
            {
                throw Problem.Of(404, "not_found");
            }

            var ledgerKey = $"admin:{key}";
            var previous = await db.WalletTransactions.AsNoTracking()
                .SingleOrDefaultAsync(t => t.PlayerId == playerId && t.LedgerKey == ledgerKey, ct);
            if (previous is not null)
            {
                // The idempotency record expired, the ledger key did not: return the stored outcome (spec §4).
                return new OperationResult(200,
                    new WalletAdjustment(previous.Id, playerId, previous.Delta, previous.BalanceAfter, previous.ReferenceId!.Value));
            }

            var balance = (await db.Wallets.FromSql($"SELECT * FROM vybe.wallets WHERE player_id = {playerId} FOR UPDATE")
                .SingleAsync(ct)).Balance;
            if (balance + delta < 0)
            {
                throw Problem.Of(409, "insufficient_funds");
            }

            var now = clock.UtcNowForDb();
            var audit = AdminAccess.Audit(db, actor, "wallet.adjust", "player", playerId.ToString(),
                new { balance }, new { balance = balance + delta, delta }, reason, now);
            var entry = await RewardService.CreditAsync(db, playerId, delta, LedgerReason.AdminAdjustment, "admin_audit_log", audit.Id, ledgerKey, ct);
            await db.SaveChangesAsync(ct);
            return new OperationResult(200, new WalletAdjustment(entry.Id, playerId, delta, entry.BalanceAfter, audit.Id));
        }, cancellationToken);
    }

    /// <summary>Newest first; filters match exactly.</summary>
    private static async Task<Page<AuditLogEntry>> ListAuditLogsAsync(
        string? entityType, string? entityId, string? actor, string? cursor, int? limit, VybeDbContext db,
        CancellationToken cancellationToken)
    {
        var take = Cursor.Limit(limit, 20);
        if (entityType is { Length: > 64 } || entityId is { Length: > 128 } || actor is { Length: > 256 })
        {
            throw Problem.ValidationFailed("/", "too_long", "a filter is too long");
        }

        var query = db.AdminAuditLogs.AsNoTracking();
        if (entityType is not null)
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (entityId is not null)
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        if (actor is not null)
        {
            query = query.Where(a => a.ActorSubject == actor);
        }

        if (Cursor.Decode(cursor) is { } decoded)
        {
            var (created, id) = ParseTimeCursor(decoded);
            query = query.Where(a => a.CreatedAt < created || (a.CreatedAt == created && a.Id.CompareTo(id) < 0));
        }

        var rows = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var items = page.Select(a => new AuditLogEntry(a.Id, a.ActorSubject, a.ActorRole, a.Action, a.EntityType, a.EntityId,
            Json(a.BeforeJson), Json(a.AfterJson), a.Reason, a.CreatedAt)).ToList();
        return new Page<AuditLogEntry>(items, rows.Count > take ? TimeCursor(page[^1].CreatedAt, page[^1].Id) : null);
    }

    /// <summary>Deleting and deleted players cannot be suspended or lifted: the status belongs to J4 then.</summary>
    private static async Task<Player> LockPlayerAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken)
    {
        var player = await db.Players.FromSql($"SELECT * FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        return player is { Status: not (PlayerStatus.Deleting or PlayerStatus.Deleted) } ? player : throw Problem.Of(404, "not_found");
    }

    private static object SuspensionState(Player player) => new { player.Status, player.SuspendedUntil, player.SuspensionReason };

    private static async Task<Dictionary<Guid, string>> HandlesAsync(VybeDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToList();
        return await db.Players.AsNoTracking().Where(p => distinct.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Handle, cancellationToken);
    }

    private static AdminReport ToResponse(PlayerReport r, Dictionary<Guid, string> handles) =>
        new(r.Id, new PlayerRef(r.ReporterPlayerId, handles[r.ReporterPlayerId]), new PlayerRef(r.TargetPlayerId, handles[r.TargetPlayerId]),
            r.Reason, r.Details, r.Status, r.CreatedAt, r.ReviewedAt);

    private static JsonElement? Json(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string TimeCursor(DateTime createdAt, Guid id) =>
        Cursor.Encode($"{createdAt.Ticks.ToString(CultureInfo.InvariantCulture)}|{id}");

    private static (DateTime CreatedAt, Guid Id) ParseTimeCursor(string decoded)
    {
        var parts = decoded.Split('|');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTime.MaxValue.Ticks && Guid.TryParse(parts[1], out var id)
            ? (new DateTime(ticks, DateTimeKind.Utc), id)
            : throw Problem.ValidationFailed("/cursor", "invalid_cursor", "cursor is not valid");
    }
}
