using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vybe.Api.Tests;

/// <summary><c>POST /guests</c>, the guest cookie, CSRF and the error shape (spec §4, §5, §11).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class GuestTests(ApiFixture api)
{
    [Fact]
    public async Task CreateGuest_Returns201AndSecureCookie_AndStoresOnlyTheTokenHash()
    {
        var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/guests");
        request.Headers.Add(Guest.CsrfHeader, Guest.CsrfValue);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var playerId = body.GetProperty("playerId").GetGuid();
        var handle = body.GetProperty("handle").GetString()!;
        Assert.Matches("^guest_[a-z0-9]{10}$", handle);
        var expiresAt = body.GetProperty("guestExpiresAt").GetDateTimeOffset();
        Assert.Equal(api.Clock.GetUtcNow().AddDays(30), expiresAt, TimeSpan.FromSeconds(1));

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("path=/api/v1", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        var token = Guest.CookieToken(response);
        Assert.Equal(32, Base64Url.DecodeFromChars(token).Length);
        var stored = await Sql.ScalarAsync<byte[]>(
            api.DataSource, "SELECT token_hash FROM vybe.guest_sessions WHERE player_id = $1", playerId);
        Assert.Equal(SHA256.HashData(Base64Url.DecodeFromChars(token)), stored);

        Assert.Equal("guest", await Sql.ScalarAsync<string>(api.DataSource, "SELECT kind FROM vybe.players WHERE id = $1", playerId));
        Assert.Equal(0L, await Sql.ScalarAsync<long>(api.DataSource, "SELECT balance FROM vybe.wallets WHERE player_id = $1", playerId));
    }

    [Fact]
    public async Task CreateGuest_WithoutCsrfHeader_Returns403()
    {
        using var response = await api.CreateClient().PostAsync("/api/v1/guests", null);
        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "csrf_check_failed");
    }

    [Fact]
    public async Task CreateGuest_WithValidGuestCookie_Returns409AndKeepsTheCookie()
    {
        var guest = await Guest.CreateAsync(api);
        using var response = await guest.SendAsync(HttpMethod.Post, "/api/v1/guests");
        await Problems.AssertAsync(response, HttpStatusCode.Conflict, "already_authenticated");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task CreateGuest_WithRevokedCookie_CreatesANewGuest()
    {
        var guest = await Guest.CreateAsync(api);
        await Sql.ExecuteAsync(api.DataSource, "UPDATE vybe.guest_sessions SET revoked_at = now() WHERE player_id = $1", guest.PlayerId);
        using var response = await guest.SendAsync(HttpMethod.Post, "/api/v1/guests");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateGuest_AboveTheRateLimit_Returns429WithRetryAfter()
    {
        await using var limited = api.CreateFactory(guestPermitsPerMinute: 1);
        var client = api.CreateClient(limited);

        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/guests");
            request.Headers.Add(Guest.CsrfHeader, Guest.CsrfValue);
            return request;
        }

        using var first = Request();
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        using var second = Request();
        using var secondResponse = await client.SendAsync(second);
        await Problems.AssertAsync(secondResponse, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.True(secondResponse.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutCookie_Returns401Problem()
    {
        var path = $"/api/v1/games/sessions/{Guid.NewGuid()}";
        using var response = await api.CreateClient().GetAsync(path);
        var body = await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Equal(path, body.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task ProtectedEndpoint_WithRevokedSession_Returns401()
    {
        var guest = await Guest.CreateAsync(api);
        await Sql.ExecuteAsync(api.DataSource, "UPDATE vybe.guest_sessions SET revoked_at = now() WHERE player_id = $1", guest.PlayerId);

        using var response = await guest.SendAsync(HttpMethod.Post, "/api/v1/games/street-hoops/sessions", "{}");
        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task ProtectedEndpoint_WithExpiredSession_Returns401()
    {
        var guest = await Guest.CreateAsync(api);
        // expires_at equal to the (fake) server time is already expired.
        await Sql.ExecuteAsync(
            api.DataSource,
            "UPDATE vybe.guest_sessions SET expires_at = $2 WHERE player_id = $1",
            guest.PlayerId,
            api.Clock.GetUtcNow().UtcDateTime);

        using var response = await guest.SendAsync(HttpMethod.Post, "/api/v1/games/street-hoops/sessions", "{}");
        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task ProtectedEndpoint_WithUnknownOrMalformedToken_Returns401(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/games/sessions/{Guid.NewGuid()}");
        request.Headers.Add("Cookie", $"vybe_guest={token}");
        using var response = await api.CreateClient().SendAsync(request);
        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task UnsafeRequest_WithCookieButNoCsrfHeader_Returns403()
    {
        var guest = await Guest.CreateAsync(api);
        using var response = await guest.SendAsync(HttpMethod.Post, "/api/v1/games/street-hoops/sessions", "{}", csrf: false);
        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "csrf_check_failed");
    }

    [Fact]
    public async Task UnknownRoute_Returns404Problem()
    {
        using var response = await api.CreateClient().GetAsync("/api/v1/nothing-here");
        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "not_found");
    }
}
