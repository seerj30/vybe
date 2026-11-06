using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;

namespace Vybe.Api.Auth;

/// <summary>
/// Guest session credentials (spec §4 <c>guest_sessions</c>): a random 256-bit token in the cookie,
/// only its SHA-256 hash in the database.
/// </summary>
internal static class GuestTokens
{
    public const string Scheme = "GuestCookie";
    public const string CookieName = "vybe_guest";
    public const string CookiePath = "/api/v1";
    public const string GuestSessionClaim = "vybe:guest_session";

    private const int TokenBytes = 32;

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Returns the stored hash of a cookie value, or null when the value cannot be a token.</summary>
    public static byte[]? Hash(string token)
    {
        if (!Base64Url.IsValid(token, out var length) || length != TokenBytes)
        {
            return null;
        }

        return SHA256.HashData(Base64Url.DecodeFromChars(token));
    }

    public static CookieOptions CookieOptions(DateTime expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = CookiePath,
        Expires = expiresAt,
        IsEssential = true,
    };

    public static Guid PlayerId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("authenticated user has no player id claim"));
}
