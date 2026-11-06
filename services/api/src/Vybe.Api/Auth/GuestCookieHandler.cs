using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vybe.Api.Http;
using Vybe.Data;

namespace Vybe.Api.Auth;

/// <summary>
/// Authenticates the <c>vybe_guest</c> cookie: the session must exist, not be revoked or expired,
/// and its player must not be deleted. Challenges and denials answer with ProblemDetails.
/// </summary>
internal sealed class GuestCookieHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    VybeDbContext db,
    TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(GuestTokens.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = GuestTokens.Hash(token);
        if (hash is null)
        {
            return AuthenticateResult.Fail("malformed guest token");
        }

        var now = clock.UtcNowMicros();
        var session = await (
                from s in db.GuestSessions
                join p in db.Players on s.PlayerId equals p.Id
                where s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now
                      && p.Status != PlayerStatus.Deleted
                select new { s.Id, s.PlayerId })
            .SingleOrDefaultAsync(Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("unknown, revoked or expired guest session");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.PlayerId.ToString()),
                new Claim(GuestTokens.GuestSessionClaim, session.Id.ToString("D", CultureInfo.InvariantCulture)),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiProblem.WriteAsync(Context, StatusCodes.Status401Unauthorized, ProblemCodes.Unauthenticated);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblem.WriteAsync(Context, StatusCodes.Status403Forbidden, ProblemCodes.Forbidden);
}
