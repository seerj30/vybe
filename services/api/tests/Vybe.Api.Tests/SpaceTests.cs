using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>Real-time spaces hub (docs/design/p2-spaces-protocol.md): auth, limits, bounds, blocks, caps, reconnect.</summary>
public sealed class SpaceTests(SpaceApiFactory api) : IClassFixture<SpaceApiFactory>, IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _connections = [];

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

        // The server has seen every close before the next test settles the state.
        var metrics = (SpaceMetrics)api.Services.GetService(typeof(SpaceMetrics))!;
        await WaitUntilAsync(() => metrics.Connections == 0);
    }

    // ------------------------------------------------------------------ auth

    [Fact]
    public async Task Ticket_NeedsAPlayer_AndOpensTheHubOnce()
    {
        using var anonymous = api.CreateBrowser();
        await Contract.AssertProblemAsync(await anonymous.PostAsync("/api/v1/spaces/tickets", null), HttpStatusCode.Unauthorized, "unauthenticated");

        var player = await Player.CreateGuestAsync(api);
        var ticket = await HubPlayer.TicketAsync(player);
        Track(await HubPlayer.ConnectWithTicketAsync(api, ticket));

        // Single use: the same ticket is refused before the upgrade.
        await AssertRefusedAsync(HttpStatusCode.Unauthorized, () => HubPlayer.ConnectWithTicketAsync(api, ticket));
    }

    [Fact]
    public async Task Ticket_IsRefused_WithoutTheCsrfHeader_OrWhenSuspended()
    {
        var player = await Player.CreateGuestAsync(api);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/spaces/tickets");
        request.Headers.Remove(Infrastructure.CsrfMiddleware.Header);
        player.Http.DefaultRequestHeaders.Remove(Infrastructure.CsrfMiddleware.Header);
        await Contract.AssertProblemAsync(await player.Http.SendAsync(request), HttpStatusCode.Forbidden, "csrf_check_failed");
        player.Http.DefaultRequestHeaders.Add(Infrastructure.CsrfMiddleware.Header, Infrastructure.CsrfMiddleware.Value);

        await SuspendAsync(player.Id);
        await Contract.AssertProblemAsync(await player.Http.PostAsync("/api/v1/spaces/tickets", null), HttpStatusCode.Forbidden, "account_suspended");
    }

    [Fact]
    public async Task Hub_RefusesNoTicket_AnUnknownTicket_AForeignOrigin_Negotiate_AndJson()
    {
        await AssertRefusedAsync(HttpStatusCode.Unauthorized, () => HubPlayer.ConnectWithTicketAsync(api, null));
        await AssertRefusedAsync(HttpStatusCode.Unauthorized, () => HubPlayer.ConnectWithTicketAsync(api, new string('a', 43)));

        var player = await Player.CreateGuestAsync(api);
        await AssertRefusedAsync(HttpStatusCode.Forbidden,
            async () => await HubPlayer.ConnectWithTicketAsync(api, await HubPlayer.TicketAsync(player), origin: "https://evil.example"));
        // The same-origin browser case works.
        Track(await HubPlayer.ConnectWithTicketAsync(api, await HubPlayer.TicketAsync(player), origin: "https://localhost"));

        // A guest cookie alone (no ticket) never opens the hub; negotiate and long polling are not served.
        Assert.Equal(HttpStatusCode.NotFound, (await player.Http.PostAsync($"{SpaceHub.Path}/negotiate?negotiateVersion=1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await player.Http.GetAsync(SpaceHub.Path)).StatusCode);

        // Production speaks MessagePack only: a JSON handshake fails.
        var other = await Player.CreateGuestAsync(api);
        await Assert.ThrowsAnyAsync<Exception>(async () => await HubPlayer.ConnectWithTicketAsync(api, await HubPlayer.TicketAsync(other), json: true));
    }

    // ------------------------------------------------------------------ join, snapshot, movement

    [Fact]
    public async Task Join_PutsPlayersTogether_AndBothSeeEachOther()
    {
        var (a, welcomeA) = await JoinAsync(await Player.CreateGuestAsync(api));
        var b = await Player.CreateGuestAsync(api);
        var (clientB, welcomeB) = await JoinAsync(b);

        Assert.Equal(1, welcomeA.ProtocolVersion);
        Assert.Equal(10, welcomeA.TickHz);
        Assert.Equal(welcomeA.InstanceId, welcomeB.InstanceId);
        Assert.NotEqual(welcomeA.Slot, welcomeB.Slot);
        Assert.Equal(2, welcomeB.Members.Count);
        var me = welcomeB.Members.Single(m => m.Slot == welcomeB.Slot);
        Assert.Equal((await b.MeAsync()).GetProperty("handle").GetString(), me.Handle);
        Assert.False(string.IsNullOrEmpty(me.Look.BodyPresetId));
        Assert.Equal("top_starter_tee", me.Look.Equipped.Top);

        var joined = await a.WaitForAsync(SpaceEvents.Joined);
        Assert.Equal(welcomeB.Slot, ((SpaceMember)joined[0]!).Slot);

        api.Tick();
        var avatars = (int[])(await clientB.WaitForAsync(SpaceEvents.Snapshot))[1]!;
        Assert.True(HubPlayer.Has(avatars, welcomeA.Slot) && HubPlayer.Has(avatars, welcomeB.Slot));
        Assert.Equal(10, avatars.Length);

        await Assert.ThrowsAsync<HubException>(() => clientB.Connection.InvokeAsync<SpaceWelcome>("Join"));
    }

    [Fact]
    public async Task Move_IsServerAuthoritative_SpeedBoundsHeadingAndIntentExpiry()
    {
        var (client, welcome) = await JoinAsync(await Player.CreateGuestAsync(api));
        api.Tick();
        var start = await client.AvatarAsync(welcome.Slot);

        // Full intent to +x: 350 cm/s × 100 ms = 35 cm per tick; heading 64 (a quarter turn); walking.
        await client.Connection.InvokeAsync("Move", 127, 0);
        api.Tick();
        var moved = await client.AvatarAsync(welcome.Slot, a => a.State == 1, start.Tick);
        Assert.Equal((start.X + 35, start.Z, 64), (moved.X, moved.Z, moved.Heading));

        // A lost client stops: the intent ends 500 ms after the last Move (5 ticks of walking in all).
        api.Tick(6);
        var stopped = await client.AvatarAsync(welcome.Slot, a => a.State == 0, moved.Tick);
        Assert.Equal(start.X + (35 * 5), stopped.X);

        // Bounds: walking long enough to +z never leaves the plaza.
        for (var i = 0; i < 120; i++)
        {
            await client.Connection.InvokeAsync("Move", 0, 127);
            api.Tick();
            if (i % 14 == 13)
            {
                api.Clock.Advance(TimeSpan.FromSeconds(1)); // keep the message budget
            }
        }

        await client.AvatarAsync(welcome.Slot, a => a.Z == SpaceRules.BoundCm, stopped.Tick);
        Assert.All(client.Events.Where(e => e.Method == SpaceEvents.Snapshot), e => Assert.All(((int[])e.Args[1]!).Chunk(5),
            entry => Assert.InRange(entry[2], -SpaceRules.BoundCm, SpaceRules.BoundCm)));

        // Out of range values are dropped with an error, not clamped.
        client.Clear();
        await client.Connection.InvokeAsync("Move", 128, 0);
        Assert.Equal("invalid_input", (await client.WaitForAsync(SpaceEvents.Error))[0]);
    }

    // ------------------------------------------------------------------ expressions

    [Fact]
    public async Task Expressions_AcceptManifestIdsAndOwnedEmotes_Only_WithTheirRate()
    {
        var player = await Player.CreateGuestAsync(api);
        var (a, welcome) = await JoinAsync(player);
        var (b, _) = await JoinAsync(await Player.CreateGuestAsync(api));

        await a.Connection.InvokeAsync("QuickChat", "hi");
        Assert.Equal([welcome.Slot, "hi"], await b.WaitForAsync(SpaceEvents.QuickChat));
        await a.Connection.InvokeAsync("Sticker", "heart");
        Assert.Equal([welcome.Slot, "heart"], await b.WaitForAsync(SpaceEvents.Sticker));

        await a.Connection.InvokeAsync("QuickChat", "thanks");
        Assert.Equal("rate_limited", (await a.WaitForAsync(SpaceEvents.Error))[0]);

        a.Clear();
        api.Clock.Advance(TimeSpan.FromSeconds(3));
        await a.Connection.InvokeAsync("QuickChat", "you are a loser"); // free text is never an id
        Assert.Equal("invalid_input", (await a.WaitForAsync(SpaceEvents.Error))[0]);
        await a.Connection.InvokeAsync("Emote", "emote_wave"); // not owned
        await a.Connection.InvokeAsync("Sticker", (string?)null);

        // After a purchase (here: a grant), RefreshLook reads the owned emotes again.
        await GrantAsync(player.Id, "emote_wave");
        await a.Connection.InvokeAsync("RefreshLook");
        Assert.Equal(welcome.Slot, (await b.WaitForAsync(SpaceEvents.Look))[0]);
        await a.Connection.InvokeAsync("Emote", "emote_wave");
        Assert.Equal([welcome.Slot, "emote_wave"], await b.WaitForAsync(SpaceEvents.Emote));
        Assert.DoesNotContain(b.Events, e => e.Method == SpaceEvents.QuickChat && (string)e.Args[1]! != "hi");
    }

    // ------------------------------------------------------------------ limits

    [Fact]
    public async Task Flooding_IsDropped_ThenKicked()
    {
        var (client, _) = await JoinAsync(await Player.CreateGuestAsync(api));
        for (var i = 0; i < 16; i++)
        {
            await client.Connection.InvokeAsync("Move", 0, 0);
        }

        Assert.Equal("rate_limited", (await client.WaitForAsync(SpaceEvents.Error))[0]);
        await IgnoreClosedAsync(async () =>
        {
            for (var i = 0; i < SpaceRules.MaxDroppedPerMinute; i++)
            {
                await client.Connection.SendAsync("Move", 0, 0);
            }
        });

        Assert.Equal("rate_limited", (await client.WaitForAsync(SpaceEvents.Kicked))[0]);
    }

    [Fact]
    public async Task InvalidInput_IsKicked_AfterTwentyInAMinute()
    {
        var (client, _) = await JoinAsync(await Player.CreateGuestAsync(api));
        await IgnoreClosedAsync(async () =>
        {
            for (var i = 0; i <= SpaceRules.MaxInvalidPerMinute; i++)
            {
                api.Clock.Advance(TimeSpan.FromSeconds(1)); // inside the message budget, inside the minute
                await client.Connection.InvokeAsync("Sticker", "no_such_sticker");
            }
        });

        Assert.Equal("invalid_input", (await client.WaitForAsync(SpaceEvents.Kicked))[0]);
    }

    [Fact]
    public async Task Connection_WithoutJoin_AndIdle_AreClosed()
    {
        var waiting = Track(await HubPlayer.ConnectAsync(api, await Player.CreateGuestAsync(api)));
        var (idle, _) = await JoinAsync(await Player.CreateGuestAsync(api));

        api.Clock.Advance(SpaceRules.JoinDeadline);
        api.Spaces.Tick();
        Assert.Equal(KickReasons.NotJoined, (await waiting.WaitForAsync(SpaceEvents.Kicked))[0]);

        api.Clock.Advance(SpaceRules.IdleTimeout);
        api.Spaces.Tick();
        Assert.Equal(KickReasons.Idle, (await idle.WaitForAsync(SpaceEvents.Kicked))[0]);
    }

    [Fact]
    public async Task Admission_CapsConnectionsPerAddress_AndInTotal()
    {
        var manager = new SpaceManager(
            new SpaceOptions { MaxConnections = 3, MaxConnectionsPerAddress = 2 },
            api.Services.GetService(typeof(QuickChatManifest)) as QuickChatManifest ?? throw new InvalidOperationException(),
            api.Clock,
            (IHubContext<SpaceHub>)api.Services.GetService(typeof(IHubContext<SpaceHub>))!,
            (SpaceMetrics)api.Services.GetService(typeof(SpaceMetrics))!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SpaceManager>.Instance);

        Assert.Equal(SpaceManager.Admission.Accepted, manager.TryAdmit("ip:a"));
        Assert.Equal(SpaceManager.Admission.Accepted, manager.TryAdmit("ip:a"));
        Assert.Equal(SpaceManager.Admission.PerAddressLimit, manager.TryAdmit("ip:a"));
        Assert.Equal(SpaceManager.Admission.Accepted, manager.TryAdmit("ip:b"));
        Assert.Equal(SpaceManager.Admission.ServerFull, manager.TryAdmit("ip:c"));
        manager.Release("ip:a");
        Assert.Equal(SpaceManager.Admission.Accepted, manager.TryAdmit("ip:c"));
    }

    // ------------------------------------------------------------------ instances

    [Fact]
    public async Task Instance_HoldsSixteen_ThenAnotherOpens_UpToTheCap()
    {
        var welcomes = new List<SpaceWelcome>();
        for (var i = 0; i < SpaceRules.InstanceCapacity * SpaceApiFactory.MaxInstances; i++)
        {
            welcomes.Add((await JoinAsync(await Player.CreateGuestAsync(api))).Welcome);
        }

        var first = welcomes[0].InstanceId;
        Assert.All(welcomes.Take(16), w => Assert.Equal(first, w.InstanceId));
        Assert.Equal(16, welcomes.Take(16).Select(w => w.Slot).Distinct().Count());
        Assert.Equal(16, welcomes[15].Members.Count);
        Assert.All(welcomes.Skip(16), w => Assert.NotEqual(first, w.InstanceId));

        // Every instance is full and the cap is reached.
        var last = Track(await HubPlayer.ConnectAsync(api, await Player.CreateGuestAsync(api)));
        var refused = await Assert.ThrowsAsync<HubException>(() => last.Connection.InvokeAsync<SpaceWelcome>("Join"));
        Assert.Contains(KickReasons.SpacesFull, refused.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ blocks

    [Fact]
    public async Task Block_BeforeJoin_KeepsThePairApart_AndBlocksNeverLockAPlayerOut()
    {
        // Four players, each blocked by all the others: each needs an instance of its own, past the cap of 2
        // (else many accounts that block one player could keep that player out of every plaza).
        var players = new List<Player>();
        for (var i = 0; i < 4; i++)
        {
            players.Add(await Player.CreateGuestAsync(api));
        }

        foreach (var blocker in players)
        {
            foreach (var other in players.Where(p => p != blocker))
            {
                await BlockAsync(blocker, other);
            }
        }

        var instances = new HashSet<int>();
        foreach (var player in players)
        {
            instances.Add((await JoinAsync(player)).Welcome.InstanceId);
        }

        Assert.Equal(4, instances.Count);
    }

    [Fact]
    public async Task Block_WhileTogether_MovesTheBlocker_TheBlockedSeesOnlyLeft()
    {
        var blocker = await Player.CreateGuestAsync(api);
        var blocked = await Player.CreateGuestAsync(api);
        var (a, welcomeA) = await JoinAsync(blocker);
        var (b, welcomeB) = await JoinAsync(blocked);
        Assert.Equal(welcomeA.InstanceId, welcomeB.InstanceId);

        await BlockAsync(blocker, blocked);

        var moved = (SpaceWelcome)(await a.WaitForAsync(SpaceEvents.Moved))[0]!;
        Assert.NotEqual(welcomeA.InstanceId, moved.InstanceId);
        Assert.Equal(welcomeA.Slot, (await b.WaitForAsync(SpaceEvents.Left))[0]);
        Assert.DoesNotContain(b.Events, e => e.Method is SpaceEvents.Moved or SpaceEvents.Kicked or SpaceEvents.Error);

        // A reconnect of the blocked player does not land with the blocker either.
        await b.DisposeAsync();
        _connections.Remove(b);
        var (_, again) = await JoinAsync(blocked);
        Assert.NotEqual(moved.InstanceId, again.InstanceId);
    }

    // ------------------------------------------------------------------ reconnect and privacy

    [Fact]
    public async Task DroppedConnection_KeepsTheSlot30Seconds_AsAway()
    {
        var dropping = await Player.CreateGuestAsync(api);
        var (a, welcomeA) = await JoinAsync(dropping);
        var (b, _) = await JoinAsync(await Player.CreateGuestAsync(api));

        await a.DisposeAsync();
        _connections.Remove(a);
        var metrics = (SpaceMetrics)api.Services.GetService(typeof(SpaceMetrics))!;
        await WaitUntilAsync(() => metrics.Connections == 1);
        api.Tick();
        await b.AvatarAsync(welcomeA.Slot, a => a.State == 2);

        // Back within 30 s: same instance and slot.
        var (_, back) = await JoinAsync(dropping);
        Assert.Equal((welcomeA.InstanceId, welcomeA.Slot), (back.InstanceId, back.Slot));

        // A second connection of the same player replaces the first; the new Join gets the slot back.
        var replaced = (await JoinAsync(dropping, join: false)).Client;
        await WaitUntilAsync(() => _connections.OfType<HubPlayer>().Any(c => c.Events.Any(e => e.Method == SpaceEvents.Kicked)));
        var again = await replaced.Connection.InvokeAsync<SpaceWelcome>("Join");
        Assert.Equal(welcomeA.Slot, again.Slot);

        // Gone for good after 30 s.
        await replaced.DisposeAsync();
        _connections.Remove(replaced);
        await WaitUntilAsync(() => metrics.Connections == 1);
        b.Clear();
        api.Clock.Advance(SpaceRules.Reservation);
        api.Spaces.Tick();
        Assert.Equal(welcomeA.Slot, (await b.WaitForAsync(SpaceEvents.Left))[0]);
    }

    [Fact]
    public async Task PrivateProfile_HasNoHandle_InTheSpace()
    {
        var player = await Player.CreateGuestAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await player.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = "private" })).StatusCode);

        var (client, welcome) = await JoinAsync(player);
        var (other, _) = await JoinAsync(await Player.CreateGuestAsync(api));

        Assert.Null(welcome.Members.Single(m => m.Slot == welcome.Slot).Handle);

        // Back to "players": RefreshLook shows the handle at once (a joined that replaces the member).
        Assert.Equal(HttpStatusCode.OK, (await player.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = "players" })).StatusCode);
        await client.Connection.InvokeAsync("RefreshLook");
        var member = (SpaceMember)(await other.WaitForAsync(SpaceEvents.Joined, a => ((SpaceMember)a[0]!).Slot == welcome.Slot))[0]!;
        Assert.Equal((await player.MeAsync()).GetProperty("handle").GetString(), member.Handle);
    }

    [Fact]
    public async Task AccountCheck_KicksSuspendedPlayers()
    {
        var player = await Player.CreateGuestAsync(api);
        var (client, _) = await JoinAsync(player);

        await SuspendAsync(player.Id);
        await api.Ticker.CheckAccountsAsync(CancellationToken.None);

        Assert.Equal(KickReasons.Suspended, (await client.WaitForAsync(SpaceEvents.Kicked))[0]);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(HubPlayer Client, SpaceWelcome Welcome)> JoinAsync(Player player, bool join = true)
    {
        var client = Track(await HubPlayer.ConnectAsync(api, player));
        return (client, join ? await client.Connection.InvokeAsync<SpaceWelcome>("Join") : null!);
    }

    private HubPlayer Track(HubPlayer client)
    {
        _connections.Add(client);
        return client;
    }

    private static uint LastTick(HubPlayer client) =>
        client.Events.Where(e => e.Method == SpaceEvents.Snapshot).Select(e => (uint)e.Args[0]!).DefaultIfEmpty(0u).Max();

    private static async Task AssertRefusedAsync(HttpStatusCode status, Func<Task> connect)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(connect);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The kick closes the connection while the test may still be sending.</summary>
    private static async Task IgnoreClosedAsync(Func<Task> send)
    {
        try
        {
            await send();
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

    private static async Task BlockAsync(Player blocker, Player blocked)
    {
        var handle = (await blocked.MeAsync()).GetProperty("handle").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await blocker.Http.PostAsJsonAsync("/api/v1/me/blocks", new { handle })).StatusCode);
    }

    private async Task SuspendAsync(Guid playerId)
    {
        await using var db = api.CreateDbContext();
        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Status, PlayerStatus.Suspended)
            .SetProperty(p => p.SuspensionReason, "test"));
    }

    private async Task GrantAsync(Guid playerId, string sku)
    {
        await using var db = api.CreateDbContext();
        var item = await db.CatalogItems.SingleAsync(i => i.Sku == sku);
        db.PlayerInventory.Add(new PlayerInventoryItem
        {
            PlayerId = playerId,
            ItemId = item.Id,
            AcquiredAt = DateTime.UtcNow,
            Source = InventorySource.AdminGrant,
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>Deploy (protocol doc §8): its own API, because shutdown stops the space for good.</summary>
public sealed class SpaceShutdownTests(SpaceApiFactory api) : IClassFixture<SpaceApiFactory>
{
    [Fact]
    public async Task Shutdown_KicksEveryone_AndRefusesNewConnections()
    {
        await using var client = await HubPlayer.ConnectAsync(api, await Player.CreateGuestAsync(api));
        await client.Connection.InvokeAsync<SpaceWelcome>("Join");

        await api.Spaces.ShutdownAsync(CancellationToken.None);

        Assert.Equal(KickReasons.ServerShutdown, (await client.WaitForAsync(SpaceEvents.Kicked))[0]);
        Assert.Equal(SpaceManager.Admission.ServerFull, api.Spaces.TryAdmit("ip:new"));
    }
}
