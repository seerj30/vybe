using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>P1 weekly leaderboard (docs/design/p1-friends-leaderboard.md).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class LeaderboardTests(ApiFactory api)
{
    [Fact]
    public async Task VerifiedRuns_ShowOnTheBoards_WithTheBestScoreAndMyRank()
    {
        var (a, aHandle) = await GuestAsync();
        var (b, bHandle) = await GuestAsync();
        await FriendsAsync(a, aHandle, b, bHandle);

        await api.PlayRunAsync(a, 8);
        await api.PlayRunAsync(b, 12);
        var board = await BoardAsync(a, "friends");
        Assert.Equal([(bHandle, 1, false), (aHandle, 2, true)], Rows(board));
        Assert.Equal(2, board.GetProperty("me").GetProperty("rank").GetInt32());
        Assert.True(board.GetProperty("me").GetProperty("listed").GetBoolean());
        var aScore = board.GetProperty("me").GetProperty("score").GetInt32();
        Assert.Equal(await ScoreAsync(a, 8), aScore);

        // A lower run keeps the best; a higher one replaces it.
        await api.PlayRunAsync(a, 6);
        Assert.Equal(aScore, (await BoardAsync(a, "friends")).GetProperty("me").GetProperty("score").GetInt32());
        await api.PlayRunAsync(a, 14);
        board = await BoardAsync(b, "friends");
        Assert.Equal([(aHandle, 1, false), (bHandle, 2, true)], Rows(board));

        var global = await BoardAsync(a, "global");
        var me = global.GetProperty("me");
        var better = global.GetProperty("entries").EnumerateArray().Count(e => e.GetProperty("score").GetInt32() > me.GetProperty("score").GetInt32());
        Assert.Equal(better + 1, me.GetProperty("rank").GetInt32());
        Assert.Equal(LeaderboardEndpoints.WeekLabel(LeaderboardEndpoints.WeekStart(api.Clock.GetUtcNow().UtcDateTime)), global.GetProperty("week").GetString());
        Assert.Equal("private, no-store", (await a.Http.GetAsync("/api/v1/leaderboards/street-hoops/weekly")).Headers.NonValidated["Cache-Control"].ToString());
    }

    [Fact]
    public async Task EqualScores_ShareARank_EarlierRunFirst()
    {
        var (a, aHandle) = await GuestAsync();
        var (b, bHandle) = await GuestAsync();
        var (c, cHandle) = await GuestAsync();
        await FriendsAsync(a, aHandle, b, bHandle);
        await FriendsAsync(a, aHandle, c, cHandle);
        await api.PlayRunAsync(b, 10);
        await api.PlayRunAsync(c, 10);
        await api.PlayRunAsync(a, 6);
        Assert.Equal([(bHandle, 1, false), (cHandle, 1, false), (aHandle, 3, true)], Rows(await BoardAsync(a, "friends")));
    }

    [Fact]
    public async Task FlaggedShortAndChallengeRecipientRuns_DoNotCount()
    {
        var (flagged, _) = await GuestAsync();
        await flagged.PlayPerfectRunAsync(); // 93 perfect shots at a fixed rhythm: review flags
        Assert.Equal(JsonValueKind.Null, (await BoardAsync(flagged, "global")).GetProperty("me").ValueKind);

        var (shortRun, _) = await GuestAsync();
        var (sessionId, seed, config, rulesetId) = await shortRun.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(10));
        var body = await Contract.AssertAsync(await shortRun.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, 10)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, (await BoardAsync(shortRun, "global")).GetProperty("me").ValueKind);

        var (creator, _) = await GuestAsync();
        var (recipient, _) = await GuestAsync();
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/challenges")
        {
            Content = JsonContent.Create(new { creatorSessionId = await api.PlayRunAsync(creator, 6) }),
        };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var challenge = await Contract.AssertAsync(await creator.Http.SendAsync(create), HttpStatusCode.Created, "ChallengeCreated");
        var token = new Uri(challenge.GetProperty("shareUrl").GetString()!).Segments[^1];
        using var accept = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invites/{token}/accept");
        accept.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, (await recipient.Http.SendAsync(accept)).StatusCode);
        await api.PlayRecipientRunAsync(recipient, challenge.GetProperty("challengeId").GetGuid(), 12);
        Assert.Equal(JsonValueKind.Null, (await BoardAsync(recipient, "global")).GetProperty("me").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, (await BoardAsync(creator, "global")).GetProperty("me").ValueKind);
    }

    [Fact]
    public async Task PrivatePlayers_OnlyOnFriendsBoards_SuspendedPlayers_Nowhere()
    {
        var (a, aHandle) = await GuestAsync();
        var (p, pHandle) = await GuestAsync();
        await FriendsAsync(a, aHandle, p, pHandle);
        await api.PlayRunAsync(p, 9);
        await Contract.AssertAsync(await p.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = "private" }), HttpStatusCode.OK, "PrivacySettings");

        var global = await BoardAsync(a, "global");
        Assert.DoesNotContain(pHandle, global.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("handle").GetString()));
        var ownGlobal = await BoardAsync(p, "global");
        Assert.False(ownGlobal.GetProperty("me").GetProperty("listed").GetBoolean());
        Assert.Contains((pHandle, 1, false), Rows(await BoardAsync(a, "friends")));

        await using (var db = api.CreateDbContext())
        {
            await db.Players.Where(x => x.Id == p.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PlayerStatus.Suspended));
        }

        Assert.Empty(Rows(await BoardAsync(a, "friends")));
        Assert.Equal(JsonValueKind.Null, (await BoardAsync(p, "friends")).GetProperty("me").ValueKind);
    }

    [Fact]
    public async Task InvalidRequests_AreRejected()
    {
        var (a, _) = await GuestAsync();
        await Contract.AssertProblemAsync(await a.Http.GetAsync("/api/v1/leaderboards/street-hoops/weekly?scope=everyone"),
            HttpStatusCode.UnprocessableEntity, "validation_failed");
        foreach (var week in new[] { "2026-46", "2025-W52", "2026-W54", "9999-W01", "2026-W00" })
        {
            await Contract.AssertProblemAsync(await a.Http.GetAsync($"/api/v1/leaderboards/street-hoops/weekly?week={week}"),
                HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        var next = LeaderboardEndpoints.WeekLabel(LeaderboardEndpoints.WeekStart(api.Clock.GetUtcNow().UtcDateTime).AddDays(7));
        await Contract.AssertProblemAsync(await a.Http.GetAsync($"/api/v1/leaderboards/street-hoops/weekly?week={next}"),
            HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await a.Http.GetAsync("/api/v1/leaderboards/no-such-game/weekly"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await a.Http.GetAsync("/api/v1/leaderboards/BAD_ID/weekly"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await api.CreateBrowser().GetAsync("/api/v1/leaderboards/street-hoops/weekly"),
            HttpStatusCode.Unauthorized, "unauthenticated");

        var first = await Contract.AssertAsync(await a.Http.GetAsync("/api/v1/leaderboards/street-hoops/weekly?week=2026-W01"), HttpStatusCode.OK, "WeeklyLeaderboard");
        Assert.Equal("2025-12-29", first.GetProperty("weekStart").GetString());
        Assert.Equal("2026-01-04", first.GetProperty("weekEnd").GetString());
    }

    [Fact]
    public void WeekStart_IsTheIsoMonday()
    {
        Assert.Equal(new DateOnly(2026, 11, 9), LeaderboardEndpoints.WeekStart(new DateTime(2026, 11, 15, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 11, 16), LeaderboardEndpoints.WeekStart(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-W46", LeaderboardEndpoints.WeekLabel(new DateOnly(2026, 11, 9)));
        Assert.Equal("2026-W53", LeaderboardEndpoints.WeekLabel(new DateOnly(2026, 12, 28)));
    }

    private async Task<(Player Player, string Handle)> GuestAsync()
    {
        var player = await Player.CreateGuestAsync(api);
        return (player, (await player.MeAsync()).GetProperty("handle").GetString()!);
    }

    private static async Task FriendsAsync(Player a, string aHandle, Player b, string bHandle)
    {
        await FriendTests.RequestOkAsync(a, bHandle);
        await Contract.AssertAsync(await FriendTests.AcceptAsync(b, aHandle), HttpStatusCode.OK, "FriendRequestResult");
    }

    private static async Task<JsonElement> BoardAsync(Player player, string scope) =>
        await Contract.AssertAsync(
            await player.Http.GetAsync($"/api/v1/leaderboards/street-hoops/weekly?scope={scope}"), HttpStatusCode.OK, "WeeklyLeaderboard");

    private static List<(string Handle, int Rank, bool IsMe)> Rows(JsonElement board) =>
        board.GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("handle").GetString()!, e.GetProperty("rank").GetInt32(), e.GetProperty("isMe").GetBoolean()))
            .ToList();

    /// <summary>The verified score of the player's latest run (it had <paramref name="shots"/> perfect shots).</summary>
    private async Task<int> ScoreAsync(Player player, int shots)
    {
        await using var db = api.CreateDbContext();
        var scores = await db.GameSessions.Where(s => s.PlayerId == player.Id && s.Status == GameSessionStatus.Verified && s.EventCount == shots)
            .Select(s => s.Score!.Value).ToListAsync();
        return Assert.Single(scores);
    }
}
