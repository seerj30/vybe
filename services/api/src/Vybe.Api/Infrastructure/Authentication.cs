using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Infrastructure;

public static class AuthSchemes
{
    public const string Player = "player";
    public const string Guest = "guest";
    public const string Supabase = "supabase";
    public const string AuthKindClaim = "vybe:auth";
}

/// <summary>Guest session tokens (spec §4 guest_sessions): random 256-bit, only the SHA-256 is stored.</summary>
public static class GuestTokens
{
    // __Host- prefix: Secure, Path=/, no Domain, so a sibling subdomain cannot overwrite it.
    public const string CookieName = "__Host-vybe_guest";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30); // provisional, spec §17 item 2

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));

    public static bool IsWellFormed(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static void SetCookie(HttpResponse response, string token, DateTime expiresAt)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresAt,
        });
    }

    public static void ClearCookie(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
    }
}

internal sealed class GuestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    VybeDbContext db,
    TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(GuestTokens.CookieName, out var token) || !GuestTokens.IsWellFormed(token))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = GuestTokens.Hash(token!);
        var now = clock.GetUtcNow().UtcDateTime;
        // A deleting or deleted player's cookie no longer authenticates (spec §2 J4, §6).
        var playerId = await db.GuestSessions.AsNoTracking()
            .Where(s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now)
            .Join(db.Players, s => s.PlayerId, p => p.Id, (s, p) => new { s.PlayerId, p.Status })
            .Where(x => x.Status != PlayerStatus.Deleting && x.Status != PlayerStatus.Deleted)
            .Select(x => (Guid?)x.PlayerId)
            .SingleOrDefaultAsync(Context.RequestAborted);

        // An expired or revoked cookie means "not signed in", so createGuest can still run.
        if (playerId is null)
        {
            return AuthenticateResult.NoResult();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, playerId.Value.ToString()), new Claim(AuthSchemes.AuthKindClaim, AuthSchemes.Guest)],
            AuthSchemes.Guest);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSchemes.Guest));
    }
}

/// <summary>Writes 401/403 as problem details instead of empty bodies.</summary>
internal sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Challenged)
        {
            await ProblemWriter.WriteAsync(context, 401, "unauthenticated");
            return;
        }

        if (authorizeResult.Forbidden)
        {
            await ProblemWriter.WriteAsync(context, 403, "forbidden");
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

/// <summary>Resolves the calling player for guest cookies and Supabase tokens (spec §5).</summary>
public sealed class CurrentPlayer(IHttpContextAccessor accessor, VybeDbContext db, TimeProvider clock)
{
    /// <summary>The caller's player id, or null for an anonymous caller or a Supabase identity without a player.</summary>
    public async Task<Guid?> TryGetIdAsync(CancellationToken cancellationToken)
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (user.FindFirstValue(AuthSchemes.AuthKindClaim) == AuthSchemes.Guest)
        {
            return Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        }

        var subject = user.FindFirstValue("sub");
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        return await db.Players.AsNoTracking()
            .Where(p => p.AuthSubject == subject && p.Status != PlayerStatus.Deleted)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>The caller's player id; 404 <c>player_not_found</c> for a Supabase identity without a player.</summary>
    public async Task<Guid> GetIdAsync(CancellationToken cancellationToken) =>
        await TryGetIdAsync(cancellationToken) ?? throw Problem.Of(404, "player_not_found");

    /// <summary>Spec §6: suspension blocks economic actions and challenge creation.</summary>
    public void EnsureNotSuspended(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (IsSuspended(player, clock.GetUtcNow().UtcDateTime))
        {
            throw Problem.Of(403, "account_suspended");
        }
    }

    public static bool IsSuspended(Player player, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.Status == PlayerStatus.Suspended && (player.SuspendedUntil is null || player.SuspendedUntil > now);
    }
}

/// <summary>
/// CSRF (spec §5/§11): cookie-authenticated unsafe requests, and createGuest, must send
/// <c>X-Requested-With: vybe-web</c>. CORS allows that header only from the web origin.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string Header = "X-Requested-With";
    public const string Value = "vybe-web";

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var unsafeMethod = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));
        var bearer = request.Headers.Authorization.Any(h => h?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true);
        var cookieAuth = request.Cookies.ContainsKey(GuestTokens.CookieName) && !bearer;
        // Routing also matches a trailing slash, so normalize before comparing.
        var path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
        var createGuest = HttpMethods.IsPost(request.Method) && path.Equals("/api/v1/guests", StringComparison.OrdinalIgnoreCase);

        if (unsafeMethod && (cookieAuth || createGuest) && request.Headers[Header] != Value)
        {
            await ProblemWriter.WriteAsync(context, 403, "csrf_check_failed");
            return;
        }

        await next(context);
    }
}
