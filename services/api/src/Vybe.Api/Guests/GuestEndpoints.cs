using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Vybe.Api.Auth;
using Vybe.Api.Http;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Guests;

internal sealed record GuestCreatedResponse(Guid PlayerId, string Handle, DateTime GuestExpiresAt);

/// <summary><c>POST /guests</c> (createGuest, spec §5).</summary>
internal static class GuestEndpoints
{
    public const string RateLimitPolicy = "guest-creation";

    private const string HandleAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int MaxAttempts = 3;

    public static void Map(RouteGroupBuilder api) =>
        api.MapPost("/guests", CreateGuestAsync)
            .AllowAnonymous()
            .RequireCsrfHeader()
            .RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// Rate-limit partition of a client address. IPv6 clients usually own a whole /64, so the limit
    /// applies per /64; otherwise rotating addresses inside it would bypass the limit.
    /// </summary>
    public static string RateLimitPartition(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static async Task<IResult> CreateGuestAsync(
        HttpContext http,
        VybeDbContext db,
        TimeProvider clock,
        IOptions<VybeApiOptions> options,
        CancellationToken cancellationToken)
    {
        // The authentication middleware already validated any guest cookie.
        if (http.User.Identity?.IsAuthenticated == true)
        {
            return ApiProblem.Result(http, StatusCodes.Status409Conflict, ProblemCodes.AlreadyAuthenticated);
        }

        for (var attempt = 1; ; attempt++)
        {
            var now = clock.UtcNowMicros();
            var expiresAt = now.AddDays(options.Value.GuestSessionDays);
            var playerId = Guid.CreateVersion7();
            var handle = "guest_" + RandomNumberGenerator.GetString(HandleAlphabet, 10);
            var token = GuestTokens.NewToken();

            db.Players.Add(new Player
            {
                Id = playerId,
                Handle = handle,
                HandleNormalized = handle,
                Kind = PlayerKind.Guest,
                Status = PlayerStatus.Active,
                Level = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Wallets.Add(new Wallet { PlayerId = playerId, Balance = 0, UpdatedAt = now });
            db.GuestSessions.Add(new GuestSession
            {
                Id = Guid.CreateVersion7(),
                PlayerId = playerId,
                TokenHash = GuestTokens.Hash(token)!,
                ExpiresAt = expiresAt,
                CreatedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts
                                               && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Random handle collision: try another one.
                db.ChangeTracker.Clear();
                continue;
            }

            http.Response.Cookies.Append(GuestTokens.CookieName, token, GuestTokens.CookieOptions(expiresAt));
            return TypedResults.Json(
                new GuestCreatedResponse(playerId, handle, expiresAt),
                statusCode: StatusCodes.Status201Created);
        }
    }
}
