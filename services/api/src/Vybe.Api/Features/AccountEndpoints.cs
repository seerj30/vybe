using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vybe.Api.Infrastructure;
using Vybe.Data;

namespace Vybe.Api.Features;

public sealed record AccountLinkRequest(string? LinkProof);

public sealed record AccountLinkedResponse(Guid PlayerId, bool AccountLinked);

/// <summary>
/// Link attempts per client address and per guest player (fixed one-minute windows), so a caller cannot
/// use the API to send many proofs to Supabase Auth (ADR 0005).
/// </summary>
public sealed class LinkRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    public LinkRateLimiter(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var perIp = configuration.GetValue("RateLimits:LinkPerIpPerMinute", 20);
        var perPlayer = configuration.GetValue("RateLimits:LinkPerPlayerPerMinute", 5);
        _limiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress.RateLimitPartition(context.Connection.RemoteIpAddress), _ => Window(perIp))),
            PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, _ => Window(perPlayer))));
    }

    public RateLimitLease Acquire(HttpContext context) => _limiter.AttemptAcquire(context);

    public void Dispose() => _limiter.Dispose();

    private static FixedWindowRateLimiterOptions Window(int permits) =>
        new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };
}

/// <summary>Spec §2 J3 account linking: the guest player keeps its id, wallet, inventory and history.</summary>
public static class AccountEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        // Guest cookie only (contract: security guestCookie); a bearer token does not authenticate here.
        api.MapPost("/accounts/link", LinkAsync).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AuthSchemes.Guest });
    }

    private static async Task<IResult> LinkAsync(
        HttpContext context, VybeDbContext db, ILinkProofVerifier verifier, LinkRateLimiter limiter, TimeProvider clock,
        CancellationToken cancellationToken)
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
        using (var lease = limiter.Acquire(context))
        {
            if (!lease.IsAcquired)
            {
                return await RetryLaterAsync(context, 429, "rate_limited", TimeSpan.FromMinutes(1));
            }
        }

        var request = RequestBody.Deserialize<AccountLinkRequest>(await RequestBody.ReadAsync(context, cancellationToken));
        if (request.LinkProof is not { Length: >= 1 and <= 8192 } proof)
        {
            throw Problem.ValidationFailed("/linkProof", "invalid", "linkProof is required (1 to 8192 characters)");
        }

        // The guest session stays valid when only the proof is rejected (ADR 0001 #11).
        var subject = await verifier.ReadSubjectAsync(proof, cancellationToken) ?? throw Problem.Of(422, "invalid_link_proof");

        // Local conflict check first: an identity that already has a player needs no call to Supabase Auth.
        if (await db.Players.AnyAsync(p => p.AuthSubject == subject, cancellationToken))
        {
            throw Problem.Of(409, "identity_already_linked");
        }

        try
        {
            if (!await verifier.IsConfirmedAsync(proof, subject, cancellationToken))
            {
                throw Problem.Of(422, "invalid_link_proof");
            }
        }
        catch (AuthUnavailableException e)
        {
            return await RetryLaterAsync(context, 503, "auth_unavailable", e.RetryAfter ?? TimeSpan.FromSeconds(30));
        }

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
        return TypedResults.Ok(new AccountLinkedResponse(playerId, true));
    }

    /// <summary>Written here, not thrown: the exception handler clears response headers such as Retry-After.</summary>
    private static async Task<IResult> RetryLaterAsync(HttpContext context, int status, string code, TimeSpan retryAfter)
    {
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        await ProblemWriter.WriteAsync(context, status, code);
        return Results.Empty;
    }
}
