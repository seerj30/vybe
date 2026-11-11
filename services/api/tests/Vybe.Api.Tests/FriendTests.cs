using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Features;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>P1 friends (docs/design/p1-friends-leaderboard.md): requests, blocks, privacy, limits, deletion.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class FriendTests(ApiFactory api)
{
    [Fact]
    public async Task Request_Accept_BothListEachOther()
    {
        var (a, aHandle) = await GuestAsync();
        var (b, bHandle) = await GuestAsync();

        Assert.Equal("pending", (await RequestOkAsync(a, bHandle.ToUpperInvariant())).GetProperty("status").GetString());
        Assert.Equal("pending", (await RequestOkAsync(a, bHandle)).GetProperty("status").GetString()); // idempotent repeat
        Assert.Equal("outgoing", await RelationAsync(a, bHandle));
        Assert.Equal("incoming", await RelationAsync(b, aHandle));
        Assert.Equal([bHandle], Handles((await RequestsAsync(a)).GetProperty("outgoing")));
        Assert.Equal([aHandle], Handles((await RequestsAsync(b)).GetProperty("incoming")));

        var accepted = await Contract.AssertAsync(await AcceptAsync(b, aHandle), HttpStatusCode.OK, "FriendRequestResult");
        Assert.Equal("accepted", accepted.GetProperty("status").GetString());
        Assert.Equal([bHandle], Handles((await FriendsAsync(a)).GetProperty("items")));
        var entry = Assert.Single((await FriendsAsync(b)).GetProperty("items").EnumerateArray());
        Assert.Equal(aHandle, entry.GetProperty("handle").GetString());
        Assert.True(entry.GetProperty("profileVisible").GetBoolean());
        Assert.Equal("friends", await RelationAsync(a, bHandle));
        Assert.Empty((await RequestsAsync(a)).GetProperty("outgoing").EnumerateArray());

        await Contract.AssertProblemAsync(await RequestAsync(a, bHandle), HttpStatusCode.Conflict, "already_friends");
        await Contract.AssertProblemAsync(await AcceptAsync(b, aHandle), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task ReverseRequest_MakesFriendsAtOnce()
    {
        var (a, aHandle) = await GuestAsync();
        var (b, bHandle) = await GuestAsync();
        await RequestOkAsync(a, bHandle);
        Assert.Equal("accepted", (await RequestOkAsync(b, aHandle)).GetProperty("status").GetString());
        Assert.Equal("friends", await RelationAsync(a, bHandle));
    }

    [Fact]
    public async Task Request_InvalidSelfUnknown_AndRelationOfUnknownIsNone()
    {
        var (a, aHandle) = await GuestAsync();
        await Contract.AssertProblemAsync(await RequestAsync(a, aHandle), HttpStatusCode.UnprocessableEntity, "self_friend_request");
        await Contract.AssertProblemAsync(await RequestAsync(a, "no_such_player_x"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await RequestAsync(a, "a-b"), HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Equal("none", await RelationAsync(a, "no_such_player_x"));
        Assert.Equal("none", await RelationAsync(a, aHandle));
        await Contract.AssertProblemAsync(await AcceptAsync(a, "no_such_player_x"), HttpStatusCode.NotFound, "not_found");

        var anonymous = api.CreateBrowser();
        await Contract.AssertProblemAsync(await anonymous.GetAsync("/api/v1/me/friends"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Decline_HidesRequest_AndRequesterWaits30Days_DeclinerMayAskAnytime()
    {
        var (a, aHandle) = await GuestAsync();
        var (b, bHandle) = await GuestAsync();
        await RequestOkAsync(a, bHandle);
        Assert.Equal(HttpStatusCode.NoContent, (await DeclineAsync(b, aHandle)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeclineAsync(b, aHandle)).StatusCode); // idempotent
        Assert.Empty((await RequestsAsync(a)).GetProperty("outgoing").EnumerateArray());
        Assert.Empty((await RequestsAsync(b)).GetProperty("incoming").EnumerateArray());
        Assert.Equal("none", await RelationAsync(a, bHandle));
        await Contract.AssertProblemAsync(await RequestAsync(a, bHandle), HttpStatusCode.Conflict, "friend_request_declined");

        await using (var db = api.CreateDbContext())
        {
            await db.Friendships.Where(f => f.RequesterPlayerId == a.Id && f.AddresseePlayerId == b.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.RespondedAt, f => f.RespondedAt!.Value.AddDays(-31)));
        }

        Assert.Equal("pending", (await RequestOkAsync(a, bHandle)).GetProperty("status").GetString());
        await DeclineAsync(b, aHandle);
        // The player who declined can ask at any time; the row now points the other way.
        Assert.Equal("pending", (await RequestOkAsync(b, aHandle)).GetProperty("status").GetString());
        Assert.Equal("incoming", await RelationAsync(a, bHandle));
    }

    [Fact]
    public async Task CancelAndRemove_AreIdempotent()
    {
        var (a, aHandle) = await GuestAsync();
        var (b, bHandle) = await GuestAsync();
        await RequestOkAsync(a, bHandle);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Http.DeleteAsync($"/api/v1/me/friend-requests/{bHandle}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Http.DeleteAsync($"/api/v1/me/friend-requests/{bHandle}")).StatusCode);
        Assert.Equal("none", await RelationAsync(b, aHandle));

        await RequestOkAsync(a, bHandle);
        // The addressee cannot "cancel" someone else's request.
        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.DeleteAsync($"/api/v1/me/friend-requests/{aHandle}")).StatusCode);
        Assert.Equal("incoming", await RelationAsync(b, aHandle));
        await Contract.AssertAsync(await AcceptAsync(b, aHandle), HttpStatusCode.OK, "FriendRequestResult");

        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.DeleteAsync($"/api/v1/me/friends/{aHandle}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.DeleteAsync($"/api/v1/me/friends/{aHandle}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.DeleteAsync("/api/v1/me/friends/no_such_player_x")).StatusCode);
        Assert.Empty((await FriendsAsync(a)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Block_EndsFriendship_AndLaterRequestsOfTheBlockedPlayerStaySuppressed()
    {
        var (a, aHandle) = await GuestAsync();
        var (b, bHandle) = await GuestAsync();
        await RequestOkAsync(a, bHandle);
        await AcceptAsync(b, aHandle);

        Assert.Equal(HttpStatusCode.NoContent, (await a.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle = bHandle })).StatusCode);
        Assert.Empty((await FriendsAsync(a)).GetProperty("items").EnumerateArray());
        Assert.Empty((await FriendsAsync(b)).GetProperty("items").EnumerateArray());

        // B cannot tell the block: the request looks pending, but A never sees it.
        Assert.Equal("pending", (await RequestOkAsync(b, aHandle)).GetProperty("status").GetString());
        Assert.Equal("outgoing", await RelationAsync(b, aHandle));
        Assert.Equal([aHandle], Handles((await RequestsAsync(b)).GetProperty("outgoing")));
        Assert.Empty((await RequestsAsync(a)).GetProperty("incoming").EnumerateArray());
        Assert.Equal("none", await RelationAsync(a, bHandle));
        await Contract.AssertProblemAsync(await AcceptAsync(a, bHandle), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await RequestAsync(a, bHandle), HttpStatusCode.UnprocessableEntity, "recipient_blocked");

        // After an unblock the suppressed request stays hidden; A's own request replaces it.
        Assert.Equal(HttpStatusCode.NoContent, (await a.Http.DeleteAsync($"/api/v1/me/blocks/{bHandle}")).StatusCode);
        Assert.Empty((await RequestsAsync(a)).GetProperty("incoming").EnumerateArray());
        Assert.Equal("pending", (await RequestOkAsync(a, bHandle)).GetProperty("status").GetString());
        Assert.Equal("incoming", await RelationAsync(b, aHandle));
    }

    [Fact]
    public async Task BlockRacingAccept_LeavesNoFriendshipAcrossTheBlock()
    {
        for (var i = 0; i < 5; i++)
        {
            var (a, aHandle) = await GuestAsync();
            var (b, bHandle) = await GuestAsync();
            await RequestOkAsync(a, bHandle);
            var accept = AcceptAsync(b, aHandle);
            var block = a.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle = bHandle });
            await Task.WhenAll(accept, block);
            Assert.Equal(HttpStatusCode.NoContent, (await block).StatusCode);
            Assert.Contains((await accept).StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound });

            await using var db = api.CreateDbContext();
            Assert.False(await db.Friendships.AnyAsync(f => (f.RequesterPlayerId == a.Id && f.AddresseePlayerId == b.Id)
                || (f.RequesterPlayerId == b.Id && f.AddresseePlayerId == a.Id)));
        }
    }

    [Fact]
    public async Task PrivateProfile_GetsNoRequests_ButMayAsk_AndFriendsMayChallengeIt()
    {
        var (a, aHandle) = await GuestAsync();
        var (c, cHandle) = await GuestAsync();
        var (d, _) = await GuestAsync();
        await Contract.AssertAsync(await c.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = "private" }),
            HttpStatusCode.OK, "PrivacySettings");

        await Contract.AssertProblemAsync(await RequestAsync(a, cHandle), HttpStatusCode.NotFound, "not_found");
        Assert.Equal("pending", (await RequestOkAsync(c, aHandle)).GetProperty("status").GetString());
        Assert.Equal("accepted", (await RequestOkAsync(a, cHandle)).GetProperty("status").GetString());
        var entry = Assert.Single((await FriendsAsync(a)).GetProperty("items").EnumerateArray());
        Assert.Equal(cHandle, entry.GetProperty("handle").GetString());
        Assert.False(entry.GetProperty("profileVisible").GetBoolean());
        await Contract.AssertProblemAsync(await a.Http.GetAsync($"/api/v1/profiles/{cHandle}"), HttpStatusCode.NotFound, "not_found");

        // A friend can challenge the private friend; a stranger still gets the 404 of an unknown handle.
        await Contract.AssertAsync(await ChallengeAsync(a, await api.PlayRunAsync(a, 10), cHandle), HttpStatusCode.Created, "ChallengeCreated");
        await Contract.AssertProblemAsync(await ChallengeAsync(d, await api.PlayRunAsync(d, 10), cHandle), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task DailyRequestLimit_CountsCancelledRequests()
    {
        var (a, _) = await GuestAsync();
        var targets = new List<string>();
        for (var i = 0; i <= FriendEndpoints.RequestsPerDay; i++)
        {
            targets.Add((await GuestAsync()).Handle);
        }

        for (var i = 0; i < FriendEndpoints.RequestsPerDay; i++)
        {
            await RequestOkAsync(a, targets[i]);
            Assert.Equal(HttpStatusCode.NoContent, (await a.Http.DeleteAsync($"/api/v1/me/friend-requests/{targets[i]}")).StatusCode);
        }

        await Contract.AssertProblemAsync(await RequestAsync(a, targets[^1]), HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.Equal("none", await RelationAsync(a, targets[^1]));
    }

    [Fact]
    public async Task OpenRequestLimit_And_SuspendedPlayersCannotAsk()
    {
        var (a, _) = await GuestAsync();
        var (target, targetHandle) = await GuestAsync();
        await using (var db = api.CreateDbContext())
        {
            // 50 open requests of earlier days (the daily counter has no row for today).
            var now = api.Clock.GetUtcNow().UtcDateTime;
            for (var i = 0; i < FriendEndpoints.MaxOutgoingPending; i++)
            {
                var other = await GuestAsync();
                db.Friendships.Add(new Friendship
                {
                    Id = Guid.CreateVersion7(),
                    RequesterPlayerId = a.Id,
                    AddresseePlayerId = other.Player.Id,
                    Status = FriendshipStatus.Pending,
                    CreatedAt = now.AddDays(-2),
                    UpdatedAt = now.AddDays(-2),
                });
            }

            await db.SaveChangesAsync();
        }

        await Contract.AssertProblemAsync(await RequestAsync(a, targetHandle), HttpStatusCode.Conflict, "friend_request_limit");
        Assert.Equal(FriendEndpoints.MaxOutgoingPending, (await RequestsAsync(a)).GetProperty("outgoing").GetArrayLength());

        await using (var db = api.CreateDbContext())
        {
            await db.Players.Where(p => p.Id == target.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlayerStatus.Suspended));
        }

        await Contract.AssertProblemAsync(await RequestAsync(target, (await GuestAsync()).Handle), HttpStatusCode.Forbidden, "account_suspended");
    }

    [Fact]
    public async Task Deletion_RemovesFriendships_AndExportListsThem()
    {
        var (a, aHandle) = await GuestAsync();
        var (b, bHandle) = await GuestAsync();
        await RequestOkAsync(a, bHandle);
        await AcceptAsync(b, aHandle);

        var exportId = (await Contract.AssertAsync(await a.Http.PostAsync("/api/v1/me/data-export", null), HttpStatusCode.Accepted, "ExportRequested"))
            .GetProperty("exportId").GetGuid();
        await api.Services.GetRequiredService<PrivacyProcessor>().RunOnceAsync(1_000, CancellationToken.None);
        var ready = await Contract.AssertAsync(await a.Http.GetAsync($"/api/v1/me/data-export/{exportId}"), HttpStatusCode.OK, "DataExport");
        using (var export = JsonDocument.Parse(await File.ReadAllBytesAsync(new Uri(ready.GetProperty("downloadUrl").GetString()!).LocalPath)))
        {
            var friendship = Assert.Single(export.RootElement.GetProperty("friendships").EnumerateArray());
            Assert.Equal(bHandle, friendship.GetProperty("otherHandle").GetString());
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Accepted, (await a.Http.SendAsync(request)).StatusCode);
        await api.Services.GetRequiredService<PrivacyProcessor>().RunOnceAsync(1_000, CancellationToken.None);

        Assert.Empty((await FriendsAsync(b)).GetProperty("items").EnumerateArray());
        await using var check = api.CreateDbContext();
        Assert.False(await check.Friendships.AnyAsync(f => f.RequesterPlayerId == a.Id || f.AddresseePlayerId == a.Id));
        Assert.False(await check.FriendRequestCounters.AnyAsync(c => c.PlayerId == a.Id));
    }

    private async Task<(Player Player, string Handle)> GuestAsync()
    {
        var player = await Player.CreateGuestAsync(api);
        return (player, (await player.MeAsync()).GetProperty("handle").GetString()!);
    }

    internal static Task<HttpResponseMessage> RequestAsync(Player player, string handle) =>
        player.Http.PostAsJsonAsync("/api/v1/me/friend-requests", new { handle });

    internal static async Task<JsonElement> RequestOkAsync(Player player, string handle) =>
        await Contract.AssertAsync(await RequestAsync(player, handle), HttpStatusCode.OK, "FriendRequestResult");

    internal static Task<HttpResponseMessage> AcceptAsync(Player player, string handle) =>
        player.Http.PostAsync($"/api/v1/me/friend-requests/{handle}/accept", null);

    private static Task<HttpResponseMessage> DeclineAsync(Player player, string handle) =>
        player.Http.PostAsync($"/api/v1/me/friend-requests/{handle}/decline", null);

    private static async Task<JsonElement> FriendsAsync(Player player) =>
        await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/friends"), HttpStatusCode.OK, "FriendList");

    private static async Task<JsonElement> RequestsAsync(Player player) =>
        await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/friend-requests"), HttpStatusCode.OK, "FriendRequestLists");

    private static async Task<string> RelationAsync(Player player, string handle) =>
        (await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/me/friendships/{handle}"), HttpStatusCode.OK, "FriendshipState"))
            .GetProperty("relation").GetString()!;

    private static List<string> Handles(JsonElement items) =>
        items.EnumerateArray().Select(e => e.GetProperty("handle").GetString()!).ToList();

    private static async Task<HttpResponseMessage> ChallengeAsync(Player player, Guid sessionId, string recipientHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/challenges")
        {
            Content = JsonContent.Create(new { creatorSessionId = sessionId, recipientHandle }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await player.Http.SendAsync(request);
    }
}
