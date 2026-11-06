using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record ExportRequested(Guid ExportId);

public sealed record DataExportResponse(Guid ExportId, ExportStatus Status, DateTime CreatedAt, string? DownloadUrl, DateTime? DownloadExpiresAt);

public sealed record DeletionRequested(Guid RequestId);

public sealed record DeletionRequestResponse(Guid RequestId, DeletionStatus Status, DateTime CreatedAt, DateTime? CompletedAt);

/// <summary>Spec §2 J4 and §5 account: data export and account deletion requests (ADR 0005).</summary>
public static class PrivacyEndpoints
{
    /// <summary>Provisional (spec §5): the newest sign-in of a registered player must be at most this old.</summary>
    public static readonly TimeSpan ReauthenticationWindow = TimeSpan.FromMinutes(5);

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/me/data-export", RequestExportAsync);
        api.MapGet("/me/data-export/{exportId:guid}", GetExportAsync);
        api.MapPost("/me/deletion-requests", RequestDeletionAsync);
        // No authentication: the deletion revokes the sessions; the random request id is the capability.
        api.MapGet("/deletion-requests/{requestId:guid}", GetDeletionAsync).AllowAnonymous();
    }

    /// <summary>One <c>queued</c>/<c>running</c> export per player; the partial unique index decides a race.</summary>
    private static async Task<IResult> RequestExportAsync(
        VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var export = new DataExportRequest
        {
            Id = Guid.CreateVersion7(),
            PlayerId = playerId,
            Status = ExportStatus.Queued,
            CreatedAt = clock.UtcNowForDb(),
        };
        db.DataExportRequests.Add(export);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            var pending = await db.DataExportRequests.AsNoTracking()
                .Where(x => x.PlayerId == playerId && (x.Status == ExportStatus.Queued || x.Status == ExportStatus.Running))
                .Select(x => x.Id)
                .SingleAsync(cancellationToken);
            throw new ApiProblemException(409, "export_in_progress", extensions: new Dictionary<string, object?> { ["exportId"] = pending });
        }

        return Results.Accepted(value: new ExportRequested(export.Id));
    }

    private static async Task<DataExportResponse> GetExportAsync(
        Guid exportId, VybeDbContext db, CurrentPlayer current, IExportStorage storage, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var export = await db.DataExportRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == exportId && x.PlayerId == playerId, cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        var downloadable = export is { Status: ExportStatus.Ready, StorageKey: not null } && clock.UtcNowForDb() < export.DownloadExpiresAt;
        return new DataExportResponse(export.Id, export.Status, export.CreatedAt,
            downloadable ? storage.DownloadUrl(export.StorageKey!, export.DownloadExpiresAt!.Value).AbsoluteUri : null,
            export.Status == ExportStatus.Ready ? export.DownloadExpiresAt : null);
    }

    /// <summary>
    /// J4: reauthentication for registered players, then <c>status = deleting</c>, the queued request
    /// (id = Idempotency-Key) and the revoked guest sessions commit together. <see cref="PrivacyProcessor"/>
    /// anonymizes the player later.
    /// </summary>
    private static async Task<IResult> RequestDeletionAsync(
        HttpContext context, VybeDbContext db, CurrentPlayer current, Idempotency idempotency, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var key = Idempotency.ReadKey(context.Request);
        if (context.User.FindFirstValue(AuthSchemes.AuthKindClaim) != AuthSchemes.Guest
            && !(NewestAuthentication(context.User) is { } signedIn && clock.GetUtcNow() - signedIn <= ReauthenticationWindow))
        {
            throw Problem.Of(403, "reauthentication_required");
        }

        var hash = Idempotency.Hash("deletion.request", string.Empty, await RequestBody.ReadAsync(context, cancellationToken));
        var result = await idempotency.RunAsync(context, playerId.ToString(), "deletion.request", hash, async ct =>
        {
            var player = await db.Players.FromSql($"SELECT * FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE").SingleAsync(ct);
            if (player.Status == PlayerStatus.Deleting)
            {
                // Already requested with another key: answer with the pending request, so the client polls it.
                var pending = await db.DeletionRequests.Where(r => r.PlayerId == playerId && r.Status != DeletionStatus.Completed)
                    .OrderBy(r => r.CreatedAt).Select(r => r.Id).FirstAsync(ct);
                return new OperationResult(202, new DeletionRequested(pending));
            }

            var now = clock.UtcNowForDb();
            player.Status = PlayerStatus.Deleting;
            player.UpdatedAt = now;
            db.DeletionRequests.Add(new DeletionRequest { Id = key, PlayerId = playerId, Status = DeletionStatus.Queued, CreatedAt = now });
            await db.SaveChangesAsync(ct);
            await db.GuestSessions.Where(s => s.PlayerId == playerId && s.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now), ct);
            return new OperationResult(202, new DeletionRequested(key));
        }, cancellationToken);

        GuestTokens.ClearCookie(context.Response);
        return result;
    }

    private static async Task<DeletionRequestResponse> GetDeletionAsync(Guid requestId, VybeDbContext db, CancellationToken cancellationToken)
    {
        var request = await db.DeletionRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        return new DeletionRequestResponse(request.Id, request.Status, request.CreatedAt, request.CompletedAt);
    }

    /// <summary>The newest <c>amr[].timestamp</c> of the Supabase token (not <c>iat</c>, which a refresh renews).</summary>
    public static DateTimeOffset? NewestAuthentication(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        DateTimeOffset? newest = null;
        foreach (var claim in user.FindAll("amr"))
        {
            try
            {
                using var entry = JsonDocument.Parse(claim.Value);
                if (entry.RootElement.ValueKind == JsonValueKind.Object
                    && entry.RootElement.TryGetProperty("timestamp", out var timestamp)
                    && timestamp.TryGetInt64(out var seconds)
                    && seconds is > 0 and < 253_402_300_800) // before year 10000
                {
                    var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    newest = newest is null || at > newest ? at : newest;
                }
            }
            catch (JsonException)
            {
                // A method name without a timestamp proves nothing about the sign-in time.
            }
        }

        return newest;
    }
}
