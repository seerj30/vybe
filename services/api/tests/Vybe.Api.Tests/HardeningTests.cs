using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>Fixes from the code and security reviews of the vertical slice.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class HardeningTests(ApiFactory api)
{
    private const string BadEvent = "[{\"tMs\":1.5,\"aimX\":0,\"power\":500}]";

    [Fact]
    public async Task InvalidEvent_ComesAfterNotStartedExpiredAndRulesetMismatch()
    {
        var player = await Player.CreateGuestAsync(api);
        var created = (await Contract.AssertAsync(
            await player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { }), HttpStatusCode.Created, "SessionCreated"));
        var rulesetId = created.GetProperty("rulesetId").GetString()!;
        var notStarted = await SubmitRawAsync(player, created.GetProperty("sessionId").GetGuid(), rulesetId, BadEvent);
        Assert.Equal("not_started", notStarted.GetProperty("rejectionReason").GetString());

        var (mismatch, _, config, _) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs));
        var wrongRuleset = await SubmitRawAsync(player, mismatch, "street-hoops-v0", BadEvent);
        Assert.Equal("ruleset_mismatch", wrongRuleset.GetProperty("rejectionReason").GetString());

        var (late, _, _, _) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + config.GraceMs + 1));
        var expired = await SubmitRawAsync(player, late, rulesetId, BadEvent);
        Assert.Equal("expired", expired.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ConcurrentCreateSession_BothSucceed_OneSessionStaysOpen()
    {
        var player = await Player.CreateGuestAsync(api);

        var responses = await Task.WhenAll(
            player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { }),
            player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.GameSessions.CountAsync(s => s.PlayerId == player.Id && s.Status == GameSessionStatus.Created));
    }

    [Fact]
    public async Task ConcurrentStart_IsIdempotent()
    {
        var player = await Player.CreateGuestAsync(api);
        var sessionId = (await Contract.AssertAsync(
            await player.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { }), HttpStatusCode.Created, "SessionCreated"))
            .GetProperty("sessionId").GetGuid();
        // A clock that is not on a whole microsecond: PostgreSQL stores microseconds, so a value
        // returned from memory and the same value read back must already agree.
        api.Clock.Advance(TimeSpan.FromTicks(7));

        var responses = await Task.WhenAll(
            player.Http.PostAsync($"/api/v1/games/sessions/{sessionId}/start", null),
            player.Http.PostAsync($"/api/v1/games/sessions/{sessionId}/start", null));

        var bodies = await Task.WhenAll(responses.Select(r => Contract.AssertAsync(r, HttpStatusCode.OK, "SessionStarted")));
        Assert.Equal(bodies[0].GetProperty("startedAt").GetDateTime(), bodies[1].GetProperty("startedAt").GetDateTime());
    }

    [Fact]
    public async Task SameKeyConcurrentPurchase_ExecutesOnce()
    {
        var player = await Player.CreateGuestAsync(api);
        await player.PlayPerfectRunAsync(); // 150 coins
        var emote = await player.CatalogItemIdAsync("acc_sweatband"); // 100 coins; one run pays 150
        var key = Guid.NewGuid();

        var responses = await Task.WhenAll(player.PurchaseAsync(emote, key), player.PurchaseAsync(emote, key));

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"unexpected {(int)r.StatusCode}"));
        var conflicts = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var conflict in conflicts)
        {
            await Contract.AssertProblemAsync(conflict, HttpStatusCode.Conflict, "request_in_progress");
        }

        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.WalletTransactions.CountAsync(t => t.PlayerId == player.Id && t.Reason == LedgerReason.Purchase));
    }

    [Fact]
    public async Task ElapsedLease_IsTakenOver_AndTheOperationRuns()
    {
        var player = await Player.CreateGuestAsync(api);
        await player.PlayPerfectRunAsync();
        var emote = await player.CatalogItemIdAsync("acc_sweatband"); // 100 coins; one run pays 150
        var key = Guid.NewGuid();
        var body = $"{{\"itemId\":\"{emote}\"}}";

        // A crashed request left an in_progress record whose lease has elapsed.
        await using (var db = api.CreateDbContext())
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vybe.idempotency_records (actor_subject, operation, key, request_hash, status, locked_until, expires_at)
                VALUES ({player.Id.ToString()}, 'purchases.create', {key},
                        {Idempotency.Hash("purchases.create", string.Empty, Encoding.UTF8.GetBytes(body))},
                        'in_progress', now() - interval '1 second', now() + interval '72 hours')
                """);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchases")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        await Contract.AssertAsync(await player.Http.SendAsync(request), HttpStatusCode.OK, "Purchase");
    }

    [Fact]
    public async Task XpCap_ClipsTheLastRun()
    {
        var player = await Player.CreateGuestAsync(api);
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        await using (var db = api.CreateDbContext())
        {
            var day = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime);
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO vybe.player_daily_counters (player_id, day, xp_from_games) VALUES ({player.Id}, {day}, 790)");
        }

        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs));
        var run = await Contract.AssertAsync(
            await player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, config.MaxEvents)), HttpStatusCode.OK, "SubmitResult");

        Assert.Equal(10, run.GetProperty("rewards").GetProperty("xp").GetInt32());
    }

    [Fact]
    public async Task SuspendedPlayer_CannotEarnOnAnOpenSession()
    {
        var player = await Player.CreateGuestAsync(api);
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        await SetStatusAsync(player.Id, PlayerStatus.Suspended);
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs));

        var response = await player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, 10));

        await Contract.AssertProblemAsync(response, HttpStatusCode.Forbidden, "account_suspended");
        Assert.Equal(0, (await player.MeAsync()).GetProperty("walletBalance").GetInt64());
    }

    [Fact]
    public async Task DeletingPlayer_CookieNoLongerAuthenticates()
    {
        var player = await Player.CreateGuestAsync(api);
        await SetStatusAsync(player.Id, PlayerStatus.Deleting);

        await Contract.AssertProblemAsync(await player.Http.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task OversizedBody_Is413OnEveryEndpoint()
    {
        var player = await Player.CreateGuestAsync(api);
        var huge = new StringContent("{\"slot\":\"" + new string('x', 20_000) + "\"}", Encoding.UTF8, "application/json");

        await Contract.AssertProblemAsync(await player.Http.PostAsync("/api/v1/me/equipment", huge), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
    }

    [Fact]
    public async Task CreateGuest_TrailingSlash_StillNeedsCsrfHeader()
    {
        var bare = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        await Contract.AssertProblemAsync(await bare.PostAsync("/api/v1/guests/", null), HttpStatusCode.Forbidden, "csrf_check_failed");
    }

    [Fact]
    public async Task LedgerCursor_OutOfRange_Is422()
    {
        var player = await Player.CreateGuestAsync(api);
        var cursor = Cursor.Encode($"{long.MaxValue}|{Guid.NewGuid()}");

        await Contract.AssertProblemAsync(
            await player.Http.GetAsync($"/api/v1/me/wallet/transactions?cursor={cursor}"), HttpStatusCode.UnprocessableEntity, "validation_failed");
    }

    private async Task SetStatusAsync(Guid playerId, PlayerStatus status)
    {
        await using var db = api.CreateDbContext();
        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, status));
    }

    private static async Task<System.Text.Json.JsonElement> SubmitRawAsync(Player player, Guid sessionId, string rulesetId, string events)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/games/sessions/{sessionId}/submit")
        {
            Content = new StringContent($$"""{"rulesetId":"{{rulesetId}}","events":{{events}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await Contract.AssertAsync(await player.Http.SendAsync(request), HttpStatusCode.OK, "SubmitResult");
    }
}

/// <summary>A separate server with a low guest limit (spec §5: createGuest is rate limited).</summary>
public sealed class RateLimitedApiFactory : ApiFactory
{
    public RateLimitedApiFactory() => GuestsPerMinute = 2;
}

public sealed class RateLimitTests(RateLimitedApiFactory api) : IClassFixture<RateLimitedApiFactory>
{
    [Fact]
    public async Task CreateGuest_IsRateLimitedPerAddress()
    {
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await api.CreateBrowser().PostAsync("/api/v1/guests", null);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
        await Contract.AssertProblemAsync(last!, HttpStatusCode.TooManyRequests, "rate_limited");
    }
}
