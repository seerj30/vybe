using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Vybe.Api.Tests;

/// <summary>A guest player and its cookie, plus helpers for the gameplay calls.</summary>
internal sealed class Guest(HttpClient client, Guid playerId, string handle, string token)
{
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "vybe-web";

    public Guid PlayerId { get; } = playerId;

    public string Handle { get; } = handle;

    public string Token { get; } = token;

    public static async Task<Guest> CreateAsync(ApiFixture api)
    {
        var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/guests");
        request.Headers.Add(CsrfHeader, CsrfValue);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Guest(
            client,
            body.GetProperty("playerId").GetGuid(),
            body.GetProperty("handle").GetString()!,
            CookieToken(response));
    }

    /// <summary>The <c>vybe_guest</c> value from the Set-Cookie header.</summary>
    public static string CookieToken(HttpResponseMessage response)
    {
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("vybe_guest=", cookie, StringComparison.Ordinal);
        return cookie["vybe_guest=".Length..cookie.IndexOf(';', StringComparison.Ordinal)];
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json = null,
        string? idempotencyKey = null,
        bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"vybe_guest={Token}");
        if (csrf)
        {
            request.Headers.Add(CsrfHeader, CsrfValue);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    public async Task<Guid> CreateSessionAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/games/street-hoops/sessions", "{}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
    }

    public async Task<JsonElement> StartAsync(Guid sessionId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/games/sessions/{sessionId}/start");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Creates and starts a session; returns its id and seed.</summary>
    public async Task<(Guid SessionId, long Seed)> StartedSessionAsync()
    {
        var sessionId = await CreateSessionAsync();
        var started = await StartAsync(sessionId);
        return (sessionId, started.GetProperty("seed").GetInt64());
    }

    public Task<HttpResponseMessage> SubmitAsync(Guid sessionId, string json, string? idempotencyKey = null) =>
        SendAsync(
            HttpMethod.Post,
            $"/api/v1/games/sessions/{sessionId}/submit",
            json,
            idempotencyKey ?? Guid.NewGuid().ToString());
}
