using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>A guest in a browser: drives the API like the web client would.</summary>
public sealed class Player(ApiFactory api, HttpClient http)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public HttpClient Http { get; } = http;

    public Guid Id { get; private set; }

    public static async Task<Player> CreateGuestAsync(ApiFactory api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var player = new Player(api, api.CreateBrowser());
        var created = await Contract.AssertAsync(await player.Http.PostAsync("/api/v1/guests", null), HttpStatusCode.Created, "GuestCreated");
        player.Id = created.GetProperty("playerId").GetGuid();
        return player;
    }

    public async Task<JsonElement> MeAsync() =>
        await Contract.AssertAsync(await Http.GetAsync("/api/v1/me"), HttpStatusCode.OK, "Me");

    public async Task<(Guid SessionId, long Seed, StreetHoopsConfig Config, string RulesetId)> StartRunAsync()
    {
        var created = await Contract.AssertAsync(
            await Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { }), HttpStatusCode.Created, "SessionCreated");
        var sessionId = created.GetProperty("sessionId").GetGuid();
        var rulesetId = created.GetProperty("rulesetId").GetString()!;
        var ruleset = await Contract.AssertAsync(await Http.GetAsync($"/api/v1/games/rulesets/{rulesetId}"), HttpStatusCode.OK, "Ruleset");
        var config = ruleset.GetProperty("config").Deserialize<StreetHoopsConfig>(Web)!;
        var started = await Contract.AssertAsync(
            await Http.PostAsync($"/api/v1/games/sessions/{sessionId}/start", null), HttpStatusCode.OK, "SessionStarted");
        return (sessionId, started.GetProperty("seed").GetInt64(), config, rulesetId);
    }

    /// <summary>Aims every shot exactly at the basket (perfect shots), every <paramref name="gapMs"/>.</summary>
    public static object[] PerfectEvents(StreetHoopsConfig config, long seed, int count, int gapMs = 650)
    {
        var waypoints = StreetHoopsSimulator.GenerateWaypoints(config, seed);
        return Enumerable.Range(0, count).Select(i =>
        {
            var t = i * gapMs;
            var (x, p) = StreetHoopsSimulator.TargetAt(config, waypoints, t);
            return (object)new { tMs = t, aimX = x, power = p };
        }).ToArray();
    }

    public async Task<HttpResponseMessage> SubmitAsync(Guid sessionId, string rulesetId, object events, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/games/sessions/{sessionId}/submit")
        {
            Content = JsonContent.Create(new { rulesetId, events, clientReportedScore = 0 }),
        };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await Http.SendAsync(request);
    }

    /// <summary>Plays one full perfect run (93 shots) and returns the verified submit body.</summary>
    public async Task<JsonElement> PlayPerfectRunAsync()
    {
        var (sessionId, seed, config, rulesetId) = await StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + 100));
        var body = await Contract.AssertAsync(
            await SubmitAsync(sessionId, rulesetId, PerfectEvents(config, seed, config.MaxEvents)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("verified", body.GetProperty("status").GetString());
        return body;
    }

    public async Task<HttpResponseMessage> PurchaseAsync(Guid itemId, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchases") { Content = JsonContent.Create(new { itemId }) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await Http.SendAsync(request);
    }

    public async Task<Guid> CatalogItemIdAsync(string sku)
    {
        await using var db = api.CreateDbContext();
        return db.CatalogItems.Single(i => i.Sku == sku).Id;
    }
}
