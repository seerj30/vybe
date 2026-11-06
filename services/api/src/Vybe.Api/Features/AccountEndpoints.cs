using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Features;

public sealed record AccountLinkRequest(string? LinkProof);

public sealed record AccountLinkedResponse(Guid PlayerId, bool AccountLinked);

/// <summary>Spec §2 J3 account linking: the guest player keeps its id, wallet, inventory and history.</summary>
public static class AccountEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        // Guest cookie only (contract: security guestCookie); a bearer token does not authenticate here.
        api.MapPost("/accounts/link", LinkAsync).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AuthSchemes.Guest });
    }

    private static async Task<AccountLinkedResponse> LinkAsync(
        HttpContext context, VybeDbContext db, ILinkProofVerifier verifier, TimeProvider clock, CancellationToken cancellationToken)
    {
        // The CSRF middleware skips requests with a bearer header, but this endpoint uses the cookie even then.
        if (context.Request.Headers[CsrfMiddleware.Header] != CsrfMiddleware.Value)
        {
            throw Problem.Of(403, "csrf_check_failed");
        }

        if (context.User.FindFirstValue(AuthSchemes.AuthKindClaim) != AuthSchemes.Guest)
        {
            throw Problem.Unauthenticated();
        }

        var playerId = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var request = RequestBody.Deserialize<AccountLinkRequest>(await RequestBody.ReadAsync(context, cancellationToken));
        if (request.LinkProof is not { Length: >= 1 and <= 8192 } proof)
        {
            throw Problem.ValidationFailed("/linkProof", "invalid", "linkProof is required (1 to 8192 characters)");
        }

        // The guest session stays valid when only the proof is rejected (ADR 0001 #11).
        var subject = await verifier.VerifyAsync(proof, cancellationToken) ?? throw Problem.Of(422, "invalid_link_proof");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var player = await db.Players
            .FromSql($"SELECT * FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE")
            .SingleAsync(cancellationToken);
        if (player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted)
        {
            throw Problem.Unauthenticated(); // a deletion request committed after this request authenticated
        }

        // Never merge: an identity that already has a player is a conflict, even a deleting one (spec §2 J3).
        if (player.Kind != PlayerKind.Guest || await db.Players.AnyAsync(p => p.AuthSubject == subject, cancellationToken))
        {
            throw Problem.Of(409, "identity_already_linked");
        }

        var now = clock.UtcNowForDb();
        player.AuthSubject = subject;
        player.Kind = PlayerKind.Registered;
        player.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Problem.Of(409, "identity_already_linked"); // a concurrent link of the same identity won
        }

        await db.GuestSessions
            .Where(s => s.PlayerId == playerId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        GuestTokens.ClearCookie(context.Response);
        return new AccountLinkedResponse(playerId, true);
    }
}
