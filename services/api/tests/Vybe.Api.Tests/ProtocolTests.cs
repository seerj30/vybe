using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>Auth, CSRF, idempotency and submit outcomes (spec §5, §6, §7).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProtocolTests(ApiFactory api)
{
    [Fact]
    public async Task CreateGuest_RequiresCsrfHeader_AndRefusesASecondGuest()
    {
        var bare = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await Contract.AssertProblemAsync(await bare.PostAsync("/api/v1/guests", null), HttpStatusCode.Forbidden, "csrf_check_failed");

        var player = await Player.CreateGuestAsync(api);
        await Contract.AssertProblemAsync(await player.Http.PostAsync("/api/v1/guests", null), HttpStatusCode.Conflict, "already_authenticated");
    }

    [Fact]
    public async Task CookieMutation_WithoutCsrfHeader_IsForbidden()
    {
        var player = await Player.CreateGuestAsync(api);
        player.Http.DefaultRequestHeaders.Remove(CsrfMiddleware.Header);

        var response = await player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { });

        await Contract.AssertProblemAsync(response, HttpStatusCode.Forbidden, "csrf_check_failed");
    }

    [Fact]
    public async Task Anonymous_GetsUnauthenticated_AndUnknownSupabaseIdentity_GetsPlayerNotFound()
    {
        var anonymous = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await Contract.AssertProblemAsync(await anonymous.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthenticated");

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.SupabaseToken("new-supabase-user"));
        await Contract.AssertProblemAsync(await anonymous.GetAsync("/api/v1/me"), HttpStatusCode.NotFound, "player_not_found");

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        await Contract.AssertProblemAsync(await anonymous.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task IdempotencyKey_MissingInvalidOrReused()
    {
        var player = await Player.CreateGuestAsync(api);
        var item = await player.CatalogItemIdAsync("emote_swish");

        var missing = await player.Http.PostAsJsonAsync("/api/v1/purchases", new { itemId = item });
        await Contract.AssertProblemAsync(missing, (HttpStatusCode)428, "idempotency_key_required");

        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchases") { Content = JsonContent.Create(new { itemId = item }) };
        invalid.Headers.Add("Idempotency-Key", "not-a-uuid");
        await Contract.AssertProblemAsync(await player.Http.SendAsync(invalid), HttpStatusCode.UnprocessableEntity, "invalid_idempotency_key");

        var key = Guid.NewGuid();
        var first = await player.PurchaseAsync(item, key);
        await Contract.AssertProblemAsync(first, HttpStatusCode.Conflict, "insufficient_funds");
        var replay = await player.PurchaseAsync(item, key);
        await Contract.AssertProblemAsync(replay, HttpStatusCode.Conflict, "insufficient_funds"); // stored 4xx replayed
        var mismatch = await player.PurchaseAsync(await player.CatalogItemIdAsync("acc_sweatband"), key);
        await Contract.AssertProblemAsync(mismatch, HttpStatusCode.UnprocessableEntity, "idempotency_key_mismatch");
    }

    [Theory]
    [InlineData("[{\"tMs\":820.0,\"aimX\":0,\"power\":500}]")]
    [InlineData("[{\"tMs\":1e3,\"aimX\":0,\"power\":500}]")]
    [InlineData("[{\"tMs\":\"120\",\"aimX\":0,\"power\":500}]")]
    [InlineData("[{\"tMs\":null,\"aimX\":0,\"power\":500}]")]
    [InlineData("[{\"tMs\":2147483648,\"aimX\":0,\"power\":500}]")]
    [InlineData("[{\"tMs\":0,\"aimX\":0}]")]
    [InlineData("[{\"tMs\":0,\"aimX\":0,\"power\":500,\"extra\":1}]")]
    [InlineData("[7]")]
    public async Task Submit_RawTokenErrors_AreRejectedAsInvalidEvent(string events)
    {
        var player = await Player.CreateGuestAsync(api);
        var (sessionId, _, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs));

        var response = await SubmitRawAsync(player, sessionId, $$"""{"rulesetId":"{{rulesetId}}","events":{{events}}}""");

        var body = await Contract.AssertAsync(response, HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("rejected", body.GetProperty("status").GetString());
        Assert.Equal("invalid_event", body.GetProperty("rejectionReason").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"rulesetId\":\"street-hoops-v1\"}")]
    [InlineData("{\"rulesetId\":1,\"events\":[]}")]
    public async Task Submit_MalformedBody_Is400_AndTheSessionStaysActive(string body)
    {
        var player = await Player.CreateGuestAsync(api);
        var (sessionId, _, _, _) = await player.StartRunAsync();

        await Contract.AssertProblemAsync(await SubmitRawAsync(player, sessionId, body), HttpStatusCode.BadRequest, "malformed_request");

        var session = await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/games/sessions/{sessionId}"), HttpStatusCode.OK, "GameSession");
        Assert.Equal("active", session.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Submit_TooEarly_TooLate_AndNotStarted()
    {
        var player = await Player.CreateGuestAsync(api);

        // A full 60 s trace submitted right after start claims more time than has passed.
        var (early, seed, config, rulesetId) = await player.StartRunAsync();
        var tooEarly = await Contract.AssertAsync(
            await player.SubmitAsync(early, rulesetId, Player.PerfectEvents(config, seed, config.MaxEvents)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("submitted_too_early", tooEarly.GetProperty("rejectionReason").GetString());

        var (late, lateSeed, _, _) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + config.GraceMs + 1));
        var tooLate = await Contract.AssertAsync(
            await player.SubmitAsync(late, rulesetId, Player.PerfectEvents(config, lateSeed, 5)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("expired", tooLate.GetProperty("status").GetString());

        var created = await Contract.AssertAsync(
            await player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { }), HttpStatusCode.Created, "SessionCreated");
        var notStarted = await Contract.AssertAsync(
            await player.SubmitAsync(created.GetProperty("sessionId").GetGuid(), rulesetId, Array.Empty<object>()), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("not_started", notStarted.GetProperty("rejectionReason").GetString());
    }

    [Fact]
    public async Task NewSession_ExpiresTheOpenOne_AndStartAfterDeadlineIsGone()
    {
        var player = await Player.CreateGuestAsync(api);
        var first = (await Contract.AssertAsync(
            await player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { }), HttpStatusCode.Created, "SessionCreated"))
            .GetProperty("sessionId").GetGuid();
        var second = (await Contract.AssertAsync(
            await player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { }), HttpStatusCode.Created, "SessionCreated"))
            .GetProperty("sessionId").GetGuid();

        var old = await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/games/sessions/{first}"), HttpStatusCode.OK, "GameSession");
        Assert.Equal("expired", old.GetProperty("status").GetString());
        await Contract.AssertProblemAsync(await player.Http.PostAsync($"/api/v1/games/sessions/{first}/start", null), HttpStatusCode.Gone, "session_expired");

        api.Clock.Advance(TimeSpan.FromMinutes(11));
        await Contract.AssertProblemAsync(await player.Http.PostAsync($"/api/v1/games/sessions/{second}/start", null), HttpStatusCode.Gone, "session_expired");
    }

    [Fact]
    public async Task Ruleset_IsPublicAndCacheable_UnknownIs404()
    {
        var anonymous = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var response = await anonymous.GetAsync("/api/v1/games/rulesets/street-hoops-v1");
        await Contract.AssertAsync(response, HttpStatusCode.OK, "Ruleset");
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        await Contract.AssertProblemAsync(await anonymous.GetAsync("/api/v1/games/rulesets/nope"), HttpStatusCode.NotFound, "not_found");
    }

    private static async Task<HttpResponseMessage> SubmitRawAsync(Player player, Guid sessionId, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/games/sessions/{sessionId}/submit")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await player.Http.SendAsync(request);
    }
}
