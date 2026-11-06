using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>
/// <c>POST /games/sessions/{id}/submit</c>: check order, raw-token validation, rewards exactly once and
/// the idempotency protocol (spec §4, §5, §7; test matrix §14 incl. E05).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SubmitTests(ApiFixture api)
{
    /// <summary>Five perfect shots (0 … 2600 ms) score 150 + 150 + 175 + 200 + 225.</summary>
    private const int FivePerfectScore = 900;
    private const long FivePerfectCoins = 30; // min(150, 20 + floor(900 / 500) * 10)
    private const int FivePerfectXp = 20; // min(80, 10 + floor(900 / 400) * 5)

    [Fact]
    public async Task Submit_PerfectRun_IsVerifiedAndRewardedOnce()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sessionId, body.GetProperty("sessionId").GetGuid());
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(FivePerfectScore, body.GetProperty("score").GetInt32());
        AssertRewards(body.GetProperty("rewards"), FivePerfectCoins, FivePerfectXp, 1);

        await AssertLedgerAsync(guest.PlayerId, balance: FivePerfectCoins, $"game:{sessionId}");
        Assert.Equal(FivePerfectXp, await Sql.ScalarAsync<long>(api.DataSource, "SELECT xp_total FROM vybe.players WHERE id = $1", guest.PlayerId));
        Assert.Equal(1, await Sql.ScalarAsync<int>(api.DataSource, "SELECT runs_rewarded FROM vybe.player_daily_counters WHERE player_id = $1", guest.PlayerId));
        Assert.Equal(FivePerfectXp, await Sql.ScalarAsync<int>(api.DataSource, "SELECT xp_from_games FROM vybe.player_daily_counters WHERE player_id = $1", guest.PlayerId));
        Assert.Equal(
            "verified|900|5|30|20",
            await Sql.ScalarAsync<string>(
                api.DataSource,
                "SELECT concat_ws('|', status, score, event_count, reward_coins, reward_xp) FROM vybe.game_sessions WHERE id = $1",
                sessionId));

        var trace = await Sql.ScalarAsync<byte[]>(api.DataSource, "SELECT compressed_trace FROM vybe.game_input_traces WHERE session_id = $1", sessionId);
        using var gzip = new GZipStream(new MemoryStream(trace), CompressionMode.Decompress);
        var events = (await JsonNode.ParseAsync(gzip))!.AsArray();
        Assert.Equal(5, events.Count);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(events.ToJsonString()))),
            await Sql.ScalarAsync<string>(api.DataSource, "SELECT input_trace_hash FROM vybe.game_sessions WHERE id = $1", sessionId));
    }

    [Fact]
    public async Task Submit_MixedRun_ScoreMatchesTheReferenceSimulator()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        var events = Traces.PerfectShots(seed, 8)
            .Select((e, i) => i switch
            {
                2 => e with { AimX = Math.Clamp(e.AimX + 400, -1000, 1000) }, // miss
                5 => e with { Power = Math.Clamp(e.Power + 90, 0, 1000) }, // hit, not perfect
                _ => e,
            })
            .ToList();
        api.Clock.Advance(TimeSpan.FromSeconds(5));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(events));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(Traces.Score(seed, events), body.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Submit_SameKeyAgain_ReplaysTheStoredResultWithoutASecondReward()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));
        var json = Traces.Body(Traces.PerfectShots(seed, 5));
        var key = Guid.NewGuid().ToString();

        using var first = await guest.SubmitAsync(sessionId, json, key);
        using var replay = await guest.SubmitAsync(sessionId, json, key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(await first.Content.ReadAsStringAsync()),
            JsonNode.Parse(await replay.Content.ReadAsStringAsync())));
        await AssertLedgerAsync(guest.PlayerId, balance: FivePerfectCoins, $"game:{sessionId}");
    }

    [Fact]
    public async Task Submit_NewKeyAfterVerification_ReturnsTheCanonicalResultWithoutASecondReward()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var first = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));
        // A different (even empty) trace cannot change a verified run.
        using var retry = await guest.SubmitAsync(sessionId, Traces.Body([]));

        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(FivePerfectScore, body.GetProperty("score").GetInt32());
        AssertRewards(body.GetProperty("rewards"), FivePerfectCoins, FivePerfectXp, 1);
        await AssertLedgerAsync(guest.PlayerId, balance: FivePerfectCoins, $"game:{sessionId}");
        Assert.Equal(1, await Sql.ScalarAsync<int>(api.DataSource, "SELECT runs_rewarded FROM vybe.player_daily_counters WHERE player_id = $1", guest.PlayerId));
    }

    [Fact]
    public async Task Submit_ConcurrentSubmitsWithDifferentKeys_RewardOnce()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));
        var json = Traces.Body(Traces.PerfectShots(seed, 5));

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => guest.SubmitAsync(sessionId, json)));

        foreach (var response in responses)
        {
            using (response)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("verified", body.GetProperty("status").GetString());
                Assert.Equal(FivePerfectScore, body.GetProperty("score").GetInt32());
            }
        }

        await AssertLedgerAsync(guest.PlayerId, balance: FivePerfectCoins, $"game:{sessionId}");
    }

    [Fact]
    public async Task Submit_SameKeyWithAnotherBody_Returns422Mismatch()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));
        var key = Guid.NewGuid().ToString();

        using var first = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)), key);
        using var second = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 4)), key);

        await Problems.AssertAsync(second, HttpStatusCode.UnprocessableEntity, "idempotency_key_mismatch");
    }

    [Fact]
    public async Task Submit_WithoutIdempotencyKey_Returns428()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        using var response = await guest.SendAsync(HttpMethod.Post, $"/api/v1/games/sessions/{sessionId}/submit", Traces.Body([]));
        await Problems.AssertAsync(response, HttpStatusCode.PreconditionRequired, "idempotency_key_required");
    }

    [Fact]
    public async Task Submit_WithNonUuidIdempotencyKey_Returns422()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        using var response = await guest.SubmitAsync(sessionId, Traces.Body([]), "not-a-uuid");
        await Problems.AssertAsync(response, HttpStatusCode.UnprocessableEntity, "invalid_idempotency_key");
    }

    [Fact]
    public async Task Submit_KeyHeldByAnotherRequest_Returns409_ThenTakesOverAfterTheLease()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));
        var json = Traces.Body(Traces.PerfectShots(seed, 5));
        var key = Guid.NewGuid();
        var now = api.Clock.GetUtcNow().UtcDateTime;
        // A request with this key crashed after taking the lease (spec §4 idempotency step 1).
        await Sql.ExecuteAsync(
            api.DataSource,
            """
            INSERT INTO vybe.idempotency_records (actor_subject, operation, key, request_hash, status, locked_until, created_at, expires_at)
            VALUES ($1, 'submitGameSession', $2, $3, 'in_progress', $4, $5, $6)
            """,
            guest.PlayerId.ToString(),
            key,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}\n{json}"))),
            now.AddSeconds(30),
            now,
            now.AddHours(72));

        using var blocked = await guest.SubmitAsync(sessionId, json, key.ToString());
        await Problems.AssertAsync(blocked, HttpStatusCode.Conflict, "request_in_progress");

        api.Clock.Advance(TimeSpan.FromSeconds(31));
        using var takeover = await guest.SubmitAsync(sessionId, json, key.ToString());
        var body = await takeover.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(
            "completed",
            await Sql.ScalarAsync<string>(api.DataSource, "SELECT status FROM vybe.idempotency_records WHERE key = $1", key));
    }

    [Theory]
    [InlineData("""{"tMs":820.0,"aimX":0,"power":500}""")]
    [InlineData("""{"tMs":1e3,"aimX":0,"power":500}""")]
    [InlineData("""{"tMs":"120","aimX":0,"power":500}""")]
    [InlineData("""{"tMs":null,"aimX":0,"power":500}""")]
    [InlineData("""{"tMs":true,"aimX":0,"power":500}""")]
    [InlineData("""{"tMs":2147483648,"aimX":0,"power":500}""")]
    [InlineData("""{"tMs":0,"aimX":-2147483648,"power":500}""")]
    [InlineData("""{"tMs":0,"aimX":0}""")]
    [InlineData("""{"tMs":0,"aimX":0,"power":500,"extra":1}""")]
    [InlineData("""{"tMs":0,"tMs":0,"aimX":0,"power":500}""")]
    [InlineData("""[0,0,500]""")]
    [InlineData("""{"tMs":0,"aimX":0,"power":500},7""")]
    public async Task Submit_InvalidEventToken_IsRejectedInvalidEvent(string rawEvents)
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(1));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(rawEvents));

        await AssertRejectedAsync(response, sessionId, "invalid_event");
        await AssertLedgerAsync(guest.PlayerId, balance: 0);
    }

    [Fact]
    public async Task Submit_BeforeStart_IsRejectedNotStarted()
    {
        var guest = await Guest.CreateAsync(api);
        var sessionId = await guest.CreateSessionAsync();

        using var response = await guest.SubmitAsync(sessionId, Traces.Body("""{"tMs":1.5}"""));

        await AssertRejectedAsync(response, sessionId, "not_started");
    }

    [Fact]
    public async Task Submit_WrongRuleset_IsRejectedRulesetMismatch_BeforeTheEventTokenCheck()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();

        using var response = await guest.SubmitAsync(sessionId, Traces.Body("""{"tMs":1.5}""", "street-hoops-v0"));

        await AssertRejectedAsync(response, sessionId, "ruleset_mismatch");
    }

    [Fact]
    public async Task Submit_AfterTheSubmitDeadline_ExpiresTheSession()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(65_001));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("expired", body.GetProperty("status").GetString());
        Assert.Equal(
            "expired||",
            await Sql.ScalarAsync<string>(
                api.DataSource,
                "SELECT concat_ws('|', status, coalesce(submitted_at::text, ''), coalesce(rejection_reason, '')) FROM vybe.game_sessions WHERE id = $1",
                sessionId));
        await AssertLedgerAsync(guest.PlayerId, balance: 0);
    }

    [Fact]
    public async Task Submit_LateWithAnInvalidEventToken_IsExpired_BecauseStep2RunsBeforeStep4()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(65_001));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body("""{"tMs":1.5}"""));

        Assert.Equal("expired", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("expired", await Sql.ScalarAsync<string>(api.DataSource, "SELECT status FROM vybe.game_sessions WHERE id = $1", sessionId));
    }

    [Fact]
    public async Task Submit_ReplacedSessionWithAnInvalidEventToken_IsExpiredAndUnchanged()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        await guest.CreateSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(1));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body("""{"tMs":1.5}"""));

        Assert.Equal("expired", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(
            "expired|",
            await Sql.ScalarAsync<string>(
                api.DataSource,
                "SELECT concat_ws('|', status, coalesce(submitted_at::text, '')) FROM vybe.game_sessions WHERE id = $1",
                sessionId));
    }

    [Fact]
    public async Task Submit_AtTheSubmitDeadline_IsStillVerified()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(65_000));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));
        Assert.Equal("verified", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Submit_TraceAheadOfTheServerClock_IsRejectedSubmittedTooEarly()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        // Last shot at 2600 ms needs at least 600 ms of server time (2000 ms tolerance).
        api.Clock.Advance(TimeSpan.FromMilliseconds(599));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        await AssertRejectedAsync(response, sessionId, "submitted_too_early");
    }

    [Theory]
    [InlineData("""{"tMs":0,"aimX":1001,"power":500}""", "out_of_range")]
    [InlineData("""{"tMs":60000,"aimX":0,"power":500}""", "out_of_range")]
    [InlineData("""{"tMs":700,"aimX":0,"power":500},{"tMs":700,"aimX":0,"power":500}""", "non_monotonic")]
    [InlineData("""{"tMs":0,"aimX":0,"power":500},{"tMs":649,"aimX":0,"power":500}""", "cadence_violation")]
    public async Task Submit_TraceBreakingTheRules_IsRejectedWithTheSimulatorReason(string rawEvents, string reason)
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(1));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(rawEvents));

        await AssertRejectedAsync(response, sessionId, reason);
    }

    [Fact]
    public async Task Submit_TooManyEvents_IsRejected()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        // 94 events fit in 60 s only with a gap below 650 ms; the count check (step 6) runs before cadence (step 8).
        var events = Enumerable.Range(0, 94).Select(i => new ShotEvent(i * 600, 0, 500)).ToList();
        api.Clock.Advance(TimeSpan.FromSeconds(60));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(events));

        await AssertRejectedAsync(response, sessionId, "too_many_events");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"rulesetId":1,"events":[]}""")]
    [InlineData("""{"rulesetId":"street-hoops-v1","events":{}}""")]
    [InlineData("""{"events":[]}""")]
    public async Task Submit_MalformedBody_Returns400AndTheSessionStaysActive(string json)
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var malformed = await guest.SubmitAsync(sessionId, json);
        await Problems.AssertAsync(malformed, HttpStatusCode.BadRequest, "malformed_request");

        using var valid = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));
        Assert.Equal("verified", (await valid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Submit_BodyAbove16KB_Returns413AndTheSessionStaysActive()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        var json = $$"""{"rulesetId":"street-hoops-v1","events":[],"clientReportedScore":"{{new string('x', 16 * 1024)}}"}""";

        using var response = await guest.SubmitAsync(sessionId, json);

        await Problems.AssertAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        Assert.Equal("active", await Sql.ScalarAsync<string>(api.DataSource, "SELECT status FROM vybe.game_sessions WHERE id = $1", sessionId));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 675)]
    public async Task Submit_FewerShotsThanMinShotsForRewards_IsVerifiedWithoutRewards(int shots, int score)
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, shots)));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(score, body.GetProperty("score").GetInt32());
        AssertRewards(body.GetProperty("rewards"), 0, 0, 1);
        await AssertLedgerAsync(guest.PlayerId, balance: 0);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(api.DataSource, "SELECT count(*) FROM vybe.player_daily_counters WHERE player_id = $1", guest.PlayerId));
    }

    [Fact]
    public async Task Submit_EleventhRewardedRunOfTheDay_GetsAQuarterOfTheCoins()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        await SetCountersAsync(guest.PlayerId, runsRewarded: 10, xpFromGames: 0);
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        AssertRewards((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rewards"), FivePerfectCoins / 4, FivePerfectXp, 1);
        Assert.Equal(11, await Sql.ScalarAsync<int>(api.DataSource, "SELECT runs_rewarded FROM vybe.player_daily_counters WHERE player_id = $1", guest.PlayerId));
    }

    [Fact]
    public async Task Submit_TwentyFirstRewardedRun_GetsNoCoinsAndNoLedgerRow()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        await SetCountersAsync(guest.PlayerId, runsRewarded: 20, xpFromGames: 0);
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        AssertRewards((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rewards"), 0, FivePerfectXp, 1);
        await AssertLedgerAsync(guest.PlayerId, balance: 0);
    }

    [Fact]
    public async Task Submit_NearTheDailyXpCap_FillsExactlyTo800()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        await SetCountersAsync(guest.PlayerId, runsRewarded: 3, xpFromGames: 790);
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        AssertRewards((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rewards"), FivePerfectCoins, 10, 1);
        Assert.Equal(800, await Sql.ScalarAsync<int>(api.DataSource, "SELECT xp_from_games FROM vybe.player_daily_counters WHERE player_id = $1", guest.PlayerId));
        Assert.Equal(10L, await Sql.ScalarAsync<long>(api.DataSource, "SELECT xp_total FROM vybe.players WHERE id = $1", guest.PlayerId));
    }

    [Fact]
    public async Task Submit_CrossingALevel_GrantsTheLevelRewardOnce()
    {
        var guest = await Guest.CreateAsync(api);
        await Sql.ExecuteAsync(api.DataSource, "UPDATE vybe.players SET xp_total = 95 WHERE id = $1", guest.PlayerId);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        AssertRewards((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rewards"), FivePerfectCoins, FivePerfectXp, 2);
        await AssertLedgerAsync(guest.PlayerId, balance: FivePerfectCoins + 100, $"game:{sessionId}", "level:2");
        Assert.Equal(2, await Sql.ScalarAsync<int>(api.DataSource, "SELECT level FROM vybe.players WHERE id = $1", guest.PlayerId));
        Assert.Equal(
            "30|130",
            await Sql.ScalarAsync<string>(
                api.DataSource,
                "SELECT string_agg(balance_after::text, '|' ORDER BY balance_after) FROM vybe.wallet_transactions WHERE player_id = $1",
                guest.PlayerId));
    }

    [Fact]
    public async Task Submit_OneGrantCrossingTwoLevels_GrantsOneRewardPerLevel()
    {
        var guest = await Guest.CreateAsync(api);
        // level is a cache of xp_total; 395 XP at level 1 lets one 20 XP grant reach level 3 (400 XP).
        await Sql.ExecuteAsync(api.DataSource, "UPDATE vybe.players SET xp_total = 395 WHERE id = $1", guest.PlayerId);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        AssertRewards((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rewards"), FivePerfectCoins, FivePerfectXp, 3);
        await AssertLedgerAsync(guest.PlayerId, balance: FivePerfectCoins + 200, $"game:{sessionId}", "level:2", "level:3");
    }

    [Fact]
    public async Task Submit_RunStartedBeforeMidnightUtc_CountsOnTheStartDay()
    {
        var guest = await Guest.CreateAsync(api);
        var now = api.Clock.GetUtcNow();
        api.Clock.Advance(now.Date.AddDays(1).AddSeconds(-10) - now.UtcDateTime);
        var startDay = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(20));

        using var response = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        Assert.Equal("verified", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(startDay, await Sql.ScalarAsync<DateOnly>(api.DataSource, "SELECT day FROM vybe.player_daily_counters WHERE player_id = $1", guest.PlayerId));
        Assert.Equal(startDay, await Sql.ScalarAsync<DateOnly>(api.DataSource, "SELECT reward_day FROM vybe.game_sessions WHERE id = $1", sessionId));
    }

    [Fact]
    public async Task GetSession_AfterVerification_ShowsScoreAndRewards()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, seed) = await guest.StartedSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(3));
        using var submit = await guest.SubmitAsync(sessionId, Traces.Body(Traces.PerfectShots(seed, 5)));

        using var response = await guest.SendAsync(HttpMethod.Get, $"/api/v1/games/sessions/{sessionId}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(FivePerfectScore, body.GetProperty("score").GetInt32());
        AssertRewards(body.GetProperty("rewards"), FivePerfectCoins, FivePerfectXp, 1);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("rejectionReason").ValueKind);
        Assert.Equal(JsonValueKind.String, body.GetProperty("startedAt").ValueKind);
    }

    [Fact]
    public async Task GetSession_AfterRejection_ShowsTheReason()
    {
        var guest = await Guest.CreateAsync(api);
        var sessionId = await guest.CreateSessionAsync();
        using var submit = await guest.SubmitAsync(sessionId, Traces.Body([]));

        using var response = await guest.SendAsync(HttpMethod.Get, $"/api/v1/games/sessions/{sessionId}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rejected", body.GetProperty("status").GetString());
        Assert.Equal("not_started", body.GetProperty("rejectionReason").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("score").ValueKind);
    }

    private static void AssertRewards(JsonElement rewards, long coins, int xp, int levelAfter)
    {
        Assert.Equal(coins, rewards.GetProperty("coins").GetInt64());
        Assert.Equal(xp, rewards.GetProperty("xp").GetInt32());
        Assert.Equal(levelAfter, rewards.GetProperty("levelAfter").GetInt32());
    }

    private async Task AssertRejectedAsync(HttpResponseMessage response, Guid sessionId, string reason)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rejected", body.GetProperty("status").GetString());
        Assert.Equal(reason, body.GetProperty("rejectionReason").GetString());
        Assert.Equal(sessionId, body.GetProperty("sessionId").GetGuid());
        Assert.Equal(
            $"rejected|{reason}",
            await Sql.ScalarAsync<string>(
                api.DataSource,
                "SELECT concat_ws('|', status, rejection_reason) FROM vybe.game_sessions WHERE id = $1",
                sessionId));
    }

    /// <summary>The wallet balance and the exact set of ledger keys of the player.</summary>
    private async Task AssertLedgerAsync(Guid playerId, long balance, params string[] ledgerKeys)
    {
        Assert.Equal(balance, await Sql.ScalarAsync<long>(api.DataSource, "SELECT balance FROM vybe.wallets WHERE player_id = $1", playerId));
        Assert.Equal(
            string.Join("|", ledgerKeys.Order(StringComparer.Ordinal)),
            await Sql.ScalarAsync<string>(
                api.DataSource,
                "SELECT coalesce(string_agg(ledger_key, '|' ORDER BY ledger_key COLLATE \"C\"), '') FROM vybe.wallet_transactions WHERE player_id = $1",
                playerId));
    }

    private Task SetCountersAsync(Guid playerId, int runsRewarded, int xpFromGames) =>
        Sql.ExecuteAsync(
            api.DataSource,
            "INSERT INTO vybe.player_daily_counters (player_id, day, runs_rewarded, xp_from_games) VALUES ($1, $2, $3, $4)",
            playerId,
            DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime),
            runsRewarded,
            xpFromGames);
}
