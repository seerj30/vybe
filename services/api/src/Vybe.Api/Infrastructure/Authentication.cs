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

    /// <summary>Spec §17 decision 2: a guest session ends this long after its last use (renewed at most once per UTC day).</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);

    /// <summary>Spec §17 decision 2: a guest session never lasts past this time after the guest player was created.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(90);

    /// <summary>The expiry for a session used at <paramref name="now"/>: the idle window, capped by the player's age.</summary>
    public static DateTime ExpiryAt(DateTime now, DateTime playerCreatedAt)
    {
        var idle = now + IdleLifetime;
        var cap = playerCreatedAt + MaxLifetime;
        return idle < cap ? idle : cap;
    }

    /// <summary>
    /// The renewed expiry, or null when no renewal is due: a session is renewed at most once per UTC day
    /// (its last renewal is <c>expiresAt - IdleLifetime</c>) and never past the cap.
    /// </summary>
    public static DateTime? RenewedExpiry(DateTime expiresAt, DateTime playerCreatedAt, DateTime now)
    {
        var lastRenewal = expiresAt - IdleLifetime;
        var renewed = ExpiryAt(now, playerCreatedAt);
        return lastRenewal < now.Date && renewed > expiresAt ? renewed : null;
    }

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
        var now = clock.UtcNowForDb();
        // A deleting or deleted player's cookie no longer authenticates (spec §2 J4, §6).
        var session = await db.GuestSessions.AsNoTracking()
            .Where(s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now)
            .Join(db.Players, s => s.PlayerId, p => p.Id, (s, p) => new { s.Id, s.PlayerId, s.ExpiresAt, p.CreatedAt, p.Status })
            .Where(x => x.Status != PlayerStatus.Deleting && x.Status != PlayerStatus.Deleted)
            .SingleOrDefaultAsync(Context.RequestAborted);

        // An expired or revoked cookie means "not signed in", so createGuest can still run.
        if (session is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (GuestTokens.RenewedExpiry(session.ExpiresAt, session.CreatedAt, now) is { } renewed)
        {
            await RenewAsync(session.Id, session.ExpiresAt, renewed, token!);
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, session.PlayerId.ToString()), new Claim(AuthSchemes.AuthKindClaim, AuthSchemes.Guest)],
            AuthSchemes.Guest);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSchemes.Guest));
    }

    /// <summary>
    /// Sliding expiry (spec §17 decision 2): one compare-and-swap write per session and UTC day. The cookie is
    /// sent when the response starts, so an error response keeps it, and a response that clears the cookie
    /// (linking, deletion) wins.
    /// </summary>
    private async Task RenewAsync(Guid sessionId, DateTime expiresAt, DateTime renewed, string token)
    {
        var updated = await db.GuestSessions
            .Where(s => s.Id == sessionId && s.ExpiresAt == expiresAt && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ExpiresAt, renewed), Context.RequestAborted);
        if (updated == 0)
        {
            return; // a parallel request renewed it and sends the cookie
        }

        var response = Response;
        response.OnStarting(() =>
        {
            if (!response.Headers.SetCookie.Any(c => c?.StartsWith(GuestTokens.CookieName + "=", StringComparison.Ordinal) == true))
            {
                GuestTokens.SetCookie(response, token, renewed);
            }

            return Task.CompletedTask;
        });
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
        if (IsSuspended(player, clock.UtcNowForDb()))
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
