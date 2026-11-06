using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Vybe.Api.Tests;

/// <summary>Session creation, start and read (spec §5 Gameplay, §6 Game session).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SessionTests(ApiFixture api)
{
    private const string CreatePath = "/api/v1/games/street-hoops/sessions";

    [Fact]
    public async Task CreateSession_Returns201WithRulesetAndNoSeed()
    {
        var guest = await Guest.CreateAsync(api);
        using var response = await guest.SendAsync(HttpMethod.Post, CreatePath, "{}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("created", body.GetProperty("status").GetString());
        Assert.Equal("street-hoops-v1", body.GetProperty("rulesetId").GetString());
        Assert.Equal("9b3bd19baa803520ab49a48f96f415f477fdfedbfc5a842dbacf00cfc8f2948d", body.GetProperty("rulesetChecksum").GetString());
        Assert.Equal(api.Clock.GetUtcNow().AddMinutes(10), body.GetProperty("startDeadline").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.False(body.TryGetProperty("seed", out _));
    }

    [Fact]
    public async Task CreateSession_ExpiresThePreviousOpenSession()
    {
        var guest = await Guest.CreateAsync(api);
        var (started, _) = await guest.StartedSessionAsync();
        var created = await guest.CreateSessionAsync();
        var newest = await guest.CreateSessionAsync();

        Assert.Equal("expired", await StatusAsync(started));
        Assert.Equal("expired", await StatusAsync(created));
        Assert.Equal("created", await StatusAsync(newest));
    }

    [Fact]
    public async Task CreateSession_WithChallengeId_Returns404ChallengeUnavailable()
    {
        var guest = await Guest.CreateAsync(api);
        using var response = await guest.SendAsync(HttpMethod.Post, CreatePath, $$"""{"challengeId":"{{Guid.NewGuid()}}"}""");
        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "challenge_unavailable");
    }

    [Theory]
    [InlineData("""{"extra":1}""", "/extra")]
    [InlineData("""{"challengeId":"not-a-uuid"}""", "/challengeId")]
    public async Task CreateSession_InvalidField_Returns422ValidationFailed(string json, string field)
    {
        var guest = await Guest.CreateAsync(api);
        using var response = await guest.SendAsync(HttpMethod.Post, CreatePath, json);
        var body = await Problems.AssertAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Equal(field, Assert.Single(body.GetProperty("errors").EnumerateArray()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task CreateSession_ManyUnknownFields_ReportsAtMost20Errors()
    {
        var guest = await Guest.CreateAsync(api);
        var json = "{" + string.Join(",", Enumerable.Range(0, 500).Select(i => $"\"f{i}\":0")) + "}";

        using var response = await guest.SendAsync(HttpMethod.Post, CreatePath, json);

        var body = await Problems.AssertAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Equal(20, body.GetProperty("errors").GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"challengeId":42}""")]
    public async Task CreateSession_MalformedBody_Returns400(string json)
    {
        var guest = await Guest.CreateAsync(api);
        using var response = await guest.SendAsync(HttpMethod.Post, CreatePath, json);
        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "malformed_request");
    }

    [Fact]
    public async Task CreateSession_SuspendedPlayer_Returns403AccountSuspended()
    {
        var guest = await Guest.CreateAsync(api);
        await Sql.ExecuteAsync(api.DataSource, "UPDATE vybe.players SET status = 'suspended' WHERE id = $1", guest.PlayerId);

        using var response = await guest.SendAsync(HttpMethod.Post, CreatePath, "{}");
        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "account_suspended");
    }

    [Fact]
    public async Task CreateSession_ElapsedSuspension_IsAllowed()
    {
        var guest = await Guest.CreateAsync(api);
        await Sql.ExecuteAsync(
            api.DataSource,
            "UPDATE vybe.players SET status = 'suspended', suspended_until = $2 WHERE id = $1",
            guest.PlayerId,
            api.Clock.GetUtcNow().UtcDateTime.AddSeconds(-1));

        await guest.CreateSessionAsync();
    }

    [Fact]
    public async Task Start_ReturnsSeedAndDeadlines_AndARepeatReturnsTheSameValues()
    {
        var guest = await Guest.CreateAsync(api);
        var sessionId = await guest.CreateSessionAsync();

        var first = await guest.StartAsync(sessionId);
        api.Clock.Advance(TimeSpan.FromSeconds(2));
        var repeat = await guest.StartAsync(sessionId);

        var seed = first.GetProperty("seed").GetInt64();
        Assert.InRange(seed, 0, uint.MaxValue);
        var startedAt = first.GetProperty("startedAt").GetDateTimeOffset();
        Assert.Equal(startedAt.AddMilliseconds(65_000), first.GetProperty("submitDeadline").GetDateTimeOffset());
        Assert.Equal(first.GetRawText(), repeat.GetRawText());
        Assert.Equal(seed, await Sql.ScalarAsync<long>(api.DataSource, "SELECT seed FROM vybe.game_sessions WHERE id = $1", sessionId));
        Assert.Equal("active", await StatusAsync(sessionId));
    }

    [Fact]
    public async Task Start_AfterTheStartDeadline_Returns410AndExpiresTheSession()
    {
        var guest = await Guest.CreateAsync(api);
        var sessionId = await guest.CreateSessionAsync();
        api.Clock.Advance(TimeSpan.FromMinutes(10));

        using var response = await guest.SendAsync(HttpMethod.Post, $"/api/v1/games/sessions/{sessionId}/start");
        await Problems.AssertAsync(response, HttpStatusCode.Gone, "session_expired");
        Assert.Equal("expired", await StatusAsync(sessionId));
    }

    [Fact]
    public async Task Start_ReplacedSession_Returns410()
    {
        var guest = await Guest.CreateAsync(api);
        var (sessionId, _) = await guest.StartedSessionAsync();
        await guest.CreateSessionAsync();

        using var response = await guest.SendAsync(HttpMethod.Post, $"/api/v1/games/sessions/{sessionId}/start");
        await Problems.AssertAsync(response, HttpStatusCode.Gone, "session_expired");
    }

    [Fact]
    public async Task OtherPlayersSession_IsNotFound()
    {
        var owner = await Guest.CreateAsync(api);
        var other = await Guest.CreateAsync(api);
        var sessionId = await owner.CreateSessionAsync();

        using var start = await other.SendAsync(HttpMethod.Post, $"/api/v1/games/sessions/{sessionId}/start");
        await Problems.AssertAsync(start, HttpStatusCode.NotFound, "not_found");
        using var get = await other.SendAsync(HttpMethod.Get, $"/api/v1/games/sessions/{sessionId}");
        await Problems.AssertAsync(get, HttpStatusCode.NotFound, "not_found");
        using var submit = await other.SubmitAsync(sessionId, Traces.Body([]));
        await Problems.AssertAsync(submit, HttpStatusCode.NotFound, "not_found");
        Assert.Equal("created", await StatusAsync(sessionId));
    }

    [Fact]
    public async Task GetSession_ReturnsTheStateWithNullFieldsAndNoSeed()
    {
        var guest = await Guest.CreateAsync(api);
        var sessionId = await guest.CreateSessionAsync();

        using var response = await guest.SendAsync(HttpMethod.Get, $"/api/v1/games/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sessionId, body.GetProperty("sessionId").GetGuid());
        Assert.Equal("street-hoops", body.GetProperty("gameId").GetString());
        Assert.Equal("created", body.GetProperty("status").GetString());
        foreach (var nullField in new[] { "challengeId", "startedAt", "submitDeadline", "score", "rewards", "rejectionReason" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(nullField).ValueKind);
        }

        Assert.False(body.TryGetProperty("seed", out _));
    }

    private Task<string> StatusAsync(Guid sessionId) =>
        Sql.ScalarAsync<string>(api.DataSource, "SELECT status FROM vybe.game_sessions WHERE id = $1", sessionId);
}
