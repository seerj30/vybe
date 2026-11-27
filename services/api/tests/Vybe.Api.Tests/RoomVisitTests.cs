using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Content;
using Vybe.Api.Features;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>Live friend room visits (docs/design/p2-room-visits.md): friends only, owner rules, blocks, privacy.</summary>
public sealed class RoomVisitTests(SpaceApiFactory api) : IClassFixture<SpaceApiFactory>, IAsyncLifetime
{
    private readonly List<HubPlayer> _connections = [];

    public Task InitializeAsync()
    {
        api.Settle();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        var metrics = api.Services.GetRequiredService<SpaceMetrics>();
        await WaitUntilAsync(() => metrics.Connections == 0);
    }

    // ------------------------------------------------------------------ open and join

    [Fact]
    public async Task OpenRoom_FriendsEnter_StrangersDoNot_AndAPrivateOwnerIsNoException()
    {
        var owner = await Player.CreateGuestAsync(api);
        var friend = await Player.CreateGuestAsync(api);
        var stranger = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, friend);
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = "private" })).StatusCode);
        var ownerHandle = await HandleAsync(owner);
        Assert.False(await RoomOpenAsync(friend, ownerHandle));

        var (host, welcome) = await OpenAsync(owner);
        Assert.Equal(SpaceKinds.Room, welcome.Space);
        Assert.Equal(welcome.Slot, welcome.OwnerSlot);
        Assert.Single(welcome.Members);
        Assert.Null(welcome.Members[0].Handle); // private owner: no handle, as in the plaza
        Assert.Equal(new SpaceBounds(-SpaceRules.RoomBoundCm, SpaceRules.RoomBoundCm, -SpaceRules.RoomBoundCm, SpaceRules.RoomBoundCm), welcome.Bounds);
        Assert.Equal(SpaceRules.RoomSpeedCmPerS, welcome.SpeedCmPerS);
        Assert.NotNull(welcome.Room);

        // Only friends see the open room, and only while it is open.
        Assert.True(await RoomOpenAsync(friend, ownerHandle));

        var (visitor, visit) = await VisitAsync(friend, ownerHandle);
        Assert.Equal((welcome.InstanceId, SpaceKinds.Room, welcome.Slot), (visit.InstanceId, visit.Space, visit.OwnerSlot));
        Assert.Equal(2, visit.Members.Count);
        Assert.Equal(visit.Slot, ((SpaceMember)(await host.WaitForAsync(SpaceEvents.Joined))[0]!).Slot);

        // A stranger gets the answer of a closed room; a bad handle is invalid input.
        var outsider = await ConnectAsync(stranger);
        await AssertHubErrorAsync(RoomErrors.Unavailable, () => outsider.Connection.InvokeAsync<SpaceWelcome>("JoinRoom", ownerHandle));
        await AssertHubErrorAsync(KickReasons.InvalidInput, () => outsider.Connection.InvokeAsync<SpaceWelcome>("JoinRoom", "no such handle!"));

        // One space at a time; expressions work as in the plaza.
        await AssertHubErrorAsync("already_joined", () => visitor.Connection.InvokeAsync<SpaceWelcome>("Join"));
        await visitor.Connection.InvokeAsync("QuickChat", "hi");
        Assert.Equal([visit.Slot, "hi"], await host.WaitForAsync(SpaceEvents.QuickChat));

        // No edits while open (visitors see the layout read at open time).
        await Contract.AssertProblemAsync(
            await owner.Http.PutAsJsonAsync("/api/v1/me/room", new { expectedRevision = 0, wallItemId = (Guid?)null, floorItemId = (Guid?)null, items = Array.Empty<object>() }),
            HttpStatusCode.Conflict, "room_open");
    }

    [Fact]
    public async Task Welcome_CarriesTheSavedLayout_AndTheRoomBoundsMatchTheManifest()
    {
        var owner = await Player.CreateGuestAsync(api);
        var lamp = await GrantAsync(owner, "furn_floor_lamp");
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.PutAsJsonAsync("/api/v1/me/room", new
        {
            expectedRevision = 0,
            wallItemId = (Guid?)null,
            floorItemId = (Guid?)null,
            items = new[] { new { itemId = lamp, surface = "floor", x = 2, y = 5, rotation = 90 } },
        })).StatusCode);

        var (_, welcome) = await OpenAsync(owner);
        Assert.Equal(new SpaceRoomItem("furn_floor_lamp", "floor", 2, 5, 90), Assert.Single(welcome.Room!.Items));
        Assert.Null(welcome.Room.Wall);

        // The walkable square stays 30 cm inside the 8 × 0.5 m floor of room.v1.json.
        var root = api.Services.GetRequiredService<ContentCatalog>().Options.Root;
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "assets", "manifests", "room.v1.json")));
        var halfCm = manifest.RootElement.GetProperty("floor").GetProperty("columns").GetInt32()
            * manifest.RootElement.GetProperty("cellMeters").GetDouble() * 100 / 2;
        Assert.Equal(SpaceRules.RoomBoundCm, halfCm - 30);
    }

    [Fact]
    public async Task Room_HoldsEight_AndMovementStaysInsideTheRoom()
    {
        var owner = await Player.CreateGuestAsync(api);
        var handle = await HandleAsync(owner);
        await OpenAsync(owner);
        var visitors = new List<(HubPlayer Client, SpaceWelcome Welcome)>();
        for (var i = 1; i < SpaceRules.RoomCapacity; i++)
        {
            var friend = await Player.CreateGuestAsync(api);
            await BefriendAsync(owner, friend);
            visitors.Add(await VisitAsync(friend, handle));
        }

        Assert.Equal(SpaceRules.RoomCapacity, visitors[^1].Welcome.Members.Count);
        var late = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, late);
        var refused = await ConnectAsync(late);
        await AssertHubErrorAsync(RoomErrors.Full, () => refused.Connection.InvokeAsync<SpaceWelcome>("JoinRoom", handle));

        // Bounds: walking long enough to +z stops 170 cm from the center (the room speed: 25 cm per tick).
        var (walker, welcome) = visitors[0];
        api.Tick();
        var start = await walker.AvatarAsync(welcome.Slot);
        await walker.Connection.InvokeAsync("Move", 0, 127);
        api.Tick();
        var moved = await walker.AvatarAsync(welcome.Slot, a => a.State == 1, start.Tick);
        Assert.Equal(Math.Min(start.Z + 25, SpaceRules.RoomBoundCm), moved.Z);
        for (var i = 0; i < 30; i++)
        {
            await walker.Connection.InvokeAsync("Move", 0, 127);
            api.Tick();
            if (i % 14 == 13)
            {
                api.Clock.Advance(TimeSpan.FromSeconds(1)); // keep the message budget
            }
        }

        await walker.AvatarAsync(welcome.Slot, a => a.Z == SpaceRules.RoomBoundCm, moved.Tick);
    }

    [Fact]
    public async Task Furniture_Blocks_AndOwnerAndVisitorsSpawnOutsideIt()
    {
        var owner = await Player.CreateGuestAsync(api);
        var friend = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, friend);
        var bed = await GrantAsync(owner, "furn_bed");
        var sofa = await GrantAsync(owner, "furn_sofa");
        var rug = await GrantAsync(owner, "furn_round_rug");
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.PutAsJsonAsync("/api/v1/me/room", new
        {
            expectedRevision = 0,
            wallItemId = (Guid?)null,
            floorItemId = (Guid?)null,
            items = new[]
            {
                new { itemId = bed, surface = "floor", x = 3, y = 2, rotation = 0 }, // over the center (the owner spawn)
                new { itemId = sofa, surface = "floor", x = 2, y = 6, rotation = 0 }, // over the visitor spawn area
                new { itemId = rug, surface = "floor", x = 0, y = 0, rotation = 0 },
            },
        })).StatusCode);

        // The owner spawns at the free point nearest to the center: the bed's left edge (x −50 minus the radius).
        var (host, welcome) = await OpenAsync(owner);
        var content = api.Services.GetRequiredService<ContentCatalog>();
        var obstacles = RoomCollision.Obstacles(welcome.Room!, content.RoomItems, content.Room);
        Assert.Equal(2, obstacles.Count); // the rug does not block
        api.Tick();
        var start = await host.AvatarAsync(welcome.Slot);
        Assert.Equal((-50 - RoomCollision.AvatarRadiusCm, 0), (start.X, start.Z));

        var (_, visit) = await VisitAsync(friend, await HandleAsync(owner));
        api.Tick();
        var guest = await host.AvatarAsync(visit.Slot);
        Assert.DoesNotContain(obstacles, o => o.Contains(guest.X, guest.Z));

        // Two ticks away from the bed, then four ticks toward it: the owner stops at its edge.
        for (var i = 0; i < 2; i++)
        {
            await host.Connection.InvokeAsync("Move", -127, 0);
            api.Tick();
        }

        var away = await host.AvatarAsync(welcome.Slot, a => a.X == start.X - 50, start.Tick);
        for (var i = 0; i < 4; i++)
        {
            await host.Connection.InvokeAsync("Move", 127, 0);
            api.Tick();
        }

        await host.Connection.InvokeAsync("Move", 0, 0);
        api.Tick();
        var stopped = await host.AvatarAsync(welcome.Slot, a => a.State == 0, away.Tick);
        Assert.Equal((start.X, start.Z), (stopped.X, stopped.Z));
    }

    // ------------------------------------------------------------------ closing

    [Fact]
    public async Task CloseRoom_AndOwnerLeave_SendVisitorsOut_AndTheRoomIsNoLongerListed()
    {
        var owner = await Player.CreateGuestAsync(api);
        var friend = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, friend);
        var handle = await HandleAsync(owner);
        var (host, _) = await OpenAsync(owner);
        var (visitor, _) = await VisitAsync(friend, handle);

        await host.Connection.InvokeAsync("CloseRoom");
        Assert.Equal(KickReasons.RoomClosed, (await visitor.WaitForAsync(SpaceEvents.Kicked))[0]);
        Assert.False(await RoomOpenAsync(friend, handle));
        Assert.DoesNotContain(host.Events, e => e.Method == SpaceEvents.Kicked);

        // The owner opens again on the same connection; Leave also closes it.
        var again = await host.Connection.InvokeAsync<SpaceWelcome>("OpenRoom");
        Assert.Equal(SpaceKinds.Room, again.Space);
        var (second, _) = await VisitAsync(friend, handle);
        await host.Connection.InvokeAsync("Leave");
        Assert.Equal(KickReasons.RoomClosed, (await second.WaitForAsync(SpaceEvents.Kicked))[0]);
        Assert.False(api.Spaces.IsRoomOpen(owner.Id));
    }

    [Fact]
    public async Task OwnerDisconnect_KeepsTheRoom30Seconds_AReconnectGetsItBack_ThenItCloses()
    {
        var owner = await Player.CreateGuestAsync(api);
        var friend = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, friend);
        var handle = await HandleAsync(owner);
        var (host, welcome) = await OpenAsync(owner);
        var (visitor, _) = await VisitAsync(friend, handle);

        await DropAsync(host);
        api.Tick();
        await visitor.AvatarAsync(welcome.Slot, a => a.State == 2); // the owner is away; the room stays open
        Assert.True(await RoomOpenAsync(friend, handle));

        var (back, again) = await OpenAsync(owner);
        Assert.Equal((welcome.InstanceId, welcome.Slot), (again.InstanceId, again.Slot));
        Assert.Equal(2, again.Members.Count);

        await DropAsync(back);
        api.Clock.Advance(SpaceRules.Reservation);
        api.Spaces.Tick();
        Assert.Equal(KickReasons.RoomClosed, (await visitor.WaitForAsync(SpaceEvents.Kicked))[0]);
        Assert.False(api.Spaces.IsRoomOpen(owner.Id));
    }

    [Fact]
    public async Task OwnerInThePlaza_ClosesTheReservedRoom()
    {
        var owner = await Player.CreateGuestAsync(api);
        var friend = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, friend);
        var (host, _) = await OpenAsync(owner);
        var (visitor, _) = await VisitAsync(friend, await HandleAsync(owner));

        await DropAsync(host);
        var (_, plaza) = await JoinPlazaAsync(owner);
        Assert.Equal(SpaceKinds.Plaza, plaza.Space);
        Assert.Null(plaza.Room);
        Assert.Equal(KickReasons.RoomClosed, (await visitor.WaitForAsync(SpaceEvents.Kicked))[0]);
    }

    // ------------------------------------------------------------------ owner rules, blocks, friendship

    [Fact]
    public async Task RemoveVisitor_IsForTheOwnerOnly_AndKeepsTheVisitorOutOfThisOpening()
    {
        var owner = await Player.CreateGuestAsync(api);
        var a = await Player.CreateGuestAsync(api);
        var b = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, a);
        await BefriendAsync(owner, b);
        var handle = await HandleAsync(owner);
        var (host, welcome) = await OpenAsync(owner);
        var (visitorA, visitA) = await VisitAsync(a, handle);
        var (visitorB, _) = await VisitAsync(b, handle);
        var memberA = visitA.Members.Single(m => m.Slot == visitA.Slot);

        // A visitor cannot remove anyone.
        await AssertHubErrorAsync(KickReasons.InvalidInput, () => visitorB.Connection.InvokeAsync("RemoveVisitor", welcome.Slot, 1));

        await host.Connection.InvokeAsync("RemoveVisitor", memberA.Slot, memberA.MemberKey);
        Assert.Equal(KickReasons.Removed, (await visitorA.WaitForAsync(SpaceEvents.Kicked))[0]);
        Assert.Equal(memberA.Slot, (await visitorB.WaitForAsync(SpaceEvents.Left))[0]);

        Assert.False(await RoomOpenAsync(a, handle));
        Assert.True(await RoomOpenAsync(b, handle));
        var retry = await ConnectAsync(a);
        await AssertHubErrorAsync(RoomErrors.Unavailable, () => retry.Connection.InvokeAsync<SpaceWelcome>("JoinRoom", handle));

        // A stale key is not found.
        api.Clock.Advance(SpaceRules.SafetyActionInterval);
        await AssertHubErrorAsync(SafetyErrors.NotFound, () => host.Connection.InvokeAsync("RemoveVisitor", memberA.Slot, memberA.MemberKey));
    }

    [Fact]
    public async Task OwnerBlock_RemovesTheVisitor_VisitorBlock_MakesTheBlockerLeave()
    {
        var owner = await Player.CreateGuestAsync(api);
        var a = await Player.CreateGuestAsync(api);
        var b = await Player.CreateGuestAsync(api);
        var c = await Player.CreateGuestAsync(api);
        foreach (var friend in new[] { a, b, c })
        {
            await BefriendAsync(owner, friend);
        }

        var handle = await HandleAsync(owner);
        var (host, _) = await OpenAsync(owner);
        var (visitorA, visitA) = await VisitAsync(a, handle);
        var (visitorB, visitB) = await VisitAsync(b, handle);
        var (visitorC, visitC) = await VisitAsync(c, handle);

        // The owner blocks A: A gets the same "removed" as a remove (A does not learn about the block).
        var memberA = visitA.Members.Single(m => m.Slot == visitA.Slot);
        await host.Connection.InvokeAsync("Block", memberA.Slot, memberA.MemberKey);
        Assert.Equal(KickReasons.Removed, (await visitorA.WaitForAsync(SpaceEvents.Kicked))[0]);
        Assert.DoesNotContain(host.Events, e => e.Method is SpaceEvents.Kicked or SpaceEvents.Moved);

        // B blocks C: B (the blocker) leaves; C sees only a left.
        var memberC = visitC.Members.Single(m => m.Slot == visitC.Slot);
        await IgnoreClosedAsync(() => visitorB.Connection.InvokeAsync("Block", memberC.Slot, memberC.MemberKey));
        Assert.Equal(KickReasons.LeftRoom, (await visitorB.WaitForAsync(SpaceEvents.Kicked))[0]);
        await visitorC.WaitForAsync(SpaceEvents.Left, e => (int)e[0]! == visitB.Slot);
        Assert.DoesNotContain(visitorC.Events, e => e.Method == SpaceEvents.Kicked);

        // B stays friends with the owner, but cannot come back while C is inside.
        var retry = await ConnectAsync(b);
        await AssertHubErrorAsync(RoomErrors.Unavailable, () => retry.Connection.InvokeAsync<SpaceWelcome>("JoinRoom", handle));
    }

    [Fact]
    public async Task FriendList_HidesAnOpenRoom_WhileAMemberIsInABlockWithTheViewer()
    {
        var owner = await Player.CreateGuestAsync(api);
        var b = await Player.CreateGuestAsync(api);
        var c = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, b);
        await BefriendAsync(owner, c);
        var handle = await HandleAsync(owner);
        await OpenAsync(owner);
        Assert.True(await RoomOpenAsync(b, handle));

        // C blocked B (B is not told): while C is inside, B sees the room as closed, the same answer as JoinRoom.
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle = await HandleAsync(b) })).StatusCode);
        var (visitorC, _) = await VisitAsync(c, handle);
        Assert.False(await RoomOpenAsync(b, handle));
        Assert.True(await RoomOpenAsync(c, handle)); // the blocker's own view: C is inside anyway

        // The other direction (B blocks a member) hides it too; after C leaves, the room shows as open again.
        await visitorC.Connection.InvokeAsync("Leave");
        Assert.True(await RoomOpenAsync(b, handle));
        var d = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, d);
        await VisitAsync(d, handle);
        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle = await HandleAsync(d) })).StatusCode);
        Assert.False(await RoomOpenAsync(b, handle));
    }

    [Fact]
    public async Task EndedFriendship_RemovesTheVisitor()
    {
        var owner = await Player.CreateGuestAsync(api);
        var friend = await Player.CreateGuestAsync(api);
        await BefriendAsync(owner, friend);
        var handle = await HandleAsync(owner);
        await OpenAsync(owner);
        var (visitor, _) = await VisitAsync(friend, handle);

        Assert.Equal(HttpStatusCode.NoContent, (await friend.Http.DeleteAsync($"/api/v1/me/friends/{handle}")).StatusCode);
        Assert.Equal(KickReasons.Removed, (await visitor.WaitForAsync(SpaceEvents.Kicked))[0]);
        Assert.False(await RoomOpenAsync(friend, handle, listed: false));
        var retry = await ConnectAsync(friend);
        await AssertHubErrorAsync(RoomErrors.Unavailable, () => retry.Connection.InvokeAsync<SpaceWelcome>("JoinRoom", handle));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(HubPlayer Client, SpaceWelcome Welcome)> OpenAsync(Player owner)
    {
        var client = await ConnectAsync(owner);
        return (client, await client.Connection.InvokeAsync<SpaceWelcome>("OpenRoom"));
    }

    private async Task<(HubPlayer Client, SpaceWelcome Welcome)> VisitAsync(Player friend, string ownerHandle)
    {
        var client = await ConnectAsync(friend);
        return (client, await client.Connection.InvokeAsync<SpaceWelcome>("JoinRoom", ownerHandle));
    }

    private async Task<(HubPlayer Client, SpaceWelcome Welcome)> JoinPlazaAsync(Player player)
    {
        var client = await ConnectAsync(player);
        return (client, await client.Connection.InvokeAsync<SpaceWelcome>("Join"));
    }

    private async Task<HubPlayer> ConnectAsync(Player player)
    {
        var client = await HubPlayer.ConnectAsync(api, player);
        _connections.Add(client);
        return client;
    }

    /// <summary>Closes a connection without <c>Leave</c> (a lost connection) and waits until the server saw it.</summary>
    private async Task DropAsync(HubPlayer client)
    {
        var metrics = api.Services.GetRequiredService<SpaceMetrics>();
        var before = metrics.Connections;
        await client.DisposeAsync();
        _connections.Remove(client);
        await WaitUntilAsync(() => metrics.Connections == before - 1);
    }

    private static async Task<string> HandleAsync(Player player) => (await player.MeAsync()).GetProperty("handle").GetString()!;

    private static async Task BefriendAsync(Player a, Player b)
    {
        Assert.Equal(HttpStatusCode.OK, (await a.Http.PostAsJsonAsync("/api/v1/me/friend-requests", new { handle = await HandleAsync(b) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Http.PostAsync($"/api/v1/me/friend-requests/{await HandleAsync(a)}/accept", null)).StatusCode);
    }

    /// <summary><c>roomOpen</c> of a friend in <c>GET /me/friends</c> (the response is checked against the OpenAPI schema).</summary>
    private static async Task<bool> RoomOpenAsync(Player viewer, string handle, bool listed = true)
    {
        var list = await Contract.AssertAsync(await viewer.Http.GetAsync("/api/v1/me/friends"), HttpStatusCode.OK, "FriendList");
        var entry = list.GetProperty("items").EnumerateArray().Where(f => f.GetProperty("handle").GetString() == handle).ToList();
        Assert.Equal(listed, entry.Count == 1);
        return listed && entry[0].GetProperty("roomOpen").GetBoolean();
    }

    private async Task<Guid> GrantAsync(Player player, string sku)
    {
        await using var db = api.CreateDbContext();
        var item = await db.CatalogItems.SingleAsync(i => i.Sku == sku);
        db.PlayerInventory.Add(new PlayerInventoryItem
        {
            PlayerId = player.Id,
            ItemId = item.Id,
            AcquiredAt = DateTime.UtcNow,
            Source = InventorySource.AdminGrant,
        });
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static async Task AssertHubErrorAsync(string code, Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<HubException>(call);
        Assert.EndsWith(code, error.Message, StringComparison.Ordinal);
    }

    /// <summary>The kick closes the connection while the call may still wait for its answer.</summary>
    private static async Task IgnoreClosedAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception exception) when (exception is InvalidOperationException or HubException or TaskCanceledException or IOException)
        {
            // closed by the server, as expected
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}
