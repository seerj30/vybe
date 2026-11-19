using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record SpaceTicket(string Ticket, DateTime ExpiresAt);

/// <summary>
/// One-time hub tickets (protocol doc §2): random 256-bit, single use, valid 30 s, in memory only (one API process,
/// P2-2). Only the SHA-256 is kept, so a memory dump does not give usable tickets.
/// </summary>
public sealed class SpaceTickets(SpaceOptions options, TimeProvider clock)
{
    /// <summary>Upper bound for live tickets (memory); far above 10 per player per minute for the connection cap.</summary>
    private const int MaxLiveTickets = 20_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (Guid PlayerId, DateTimeOffset ExpiresAt)> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _issued = [];

    /// <summary>A new ticket, or null when the player asked for too many in the last minute.</summary>
    public SpaceTicket? Issue(Guid playerId)
    {
        var now = clock.GetUtcNow();
        var token = GuestTokens.NewToken();
        lock (_gate)
        {
            if (_tickets.Count >= MaxLiveTickets / 2)
            {
                Sweep(now);
            }

            if (!_issued.TryGetValue(playerId, out var times))
            {
                times = new Queue<DateTimeOffset>();
                _issued[playerId] = times;
            }

            while (times.Count > 0 && times.Peek() <= now - TimeSpan.FromMinutes(1))
            {
                times.Dequeue();
            }

            if (times.Count >= options.TicketsPerMinute || _tickets.Count >= MaxLiveTickets)
            {
                return null;
            }

            times.Enqueue(now);
            var expiresAt = now + SpaceRules.TicketLifetime;
            _tickets[Key(token)] = (playerId, expiresAt);
            return new SpaceTicket(token, expiresAt.UtcDateTime);
        }
    }

    /// <summary>The ticket's player, once: a used, unknown or expired ticket gives null.</summary>
    public Guid? Redeem(string? token)
    {
        if (!GuestTokens.IsWellFormed(token))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        lock (_gate)
        {
            return _tickets.Remove(Key(token!), out var ticket) && ticket.ExpiresAt > now ? ticket.PlayerId : null;
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, ticket) in _tickets.Where(t => t.Value.ExpiresAt <= now).ToList())
        {
            _tickets.Remove(key);
        }

        foreach (var (player, times) in _issued.Where(p => p.Value.Count == 0 || p.Value.Last() <= now - TimeSpan.FromMinutes(1)).ToList())
        {
            _issued.Remove(player);
        }
    }

    private static string Key(string token) => Convert.ToHexString(GuestTokens.Hash(token));
}

/// <summary><c>POST /spaces/tickets</c> (protocol doc §2): the normal guest-cookie or bearer auth, CSRF and write limits apply.</summary>
public static class SpaceTicketEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/spaces/tickets", CreateAsync);
    }

    private static async Task<IResult> CreateAsync(
        VybeDbContext db, CurrentPlayer current, SpaceTickets tickets, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, cancellationToken);
        if (player.Status is PlayerStatus.Deleting or PlayerStatus.Deleted)
        {
            throw Problem.Of(401, "unauthenticated");
        }

        current.EnsureNotSuspended(player);
        var ticket = tickets.Issue(playerId) ?? throw Problem.Of(429, "rate_limited");
        return TypedResults.Created((string?)null, ticket);
    }
}

/// <summary>
/// The hub's only authentication scheme: the one-time ticket in <c>access_token</c>. The guest cookie or a bearer
/// token alone never opens the hub (cross-site WebSocket hijacking, protocol doc §2).
/// </summary>
internal sealed class SpaceTicketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SpaceTickets tickets) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "space-ticket";
    public const string QueryKey = "access_token";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Query[QueryKey];
        if (values.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (tickets.Redeem(values[0]) is not { } playerId)
        {
            return Task.FromResult(AuthenticateResult.Fail("invalid space ticket"));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, playerId.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
