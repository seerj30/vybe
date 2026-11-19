using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vybe.Api.Features;

namespace Vybe.Api.Tests;

/// <summary>The API with the tick loop off (tests call <see cref="SpaceManager.Tick"/>) and small caps.</summary>
public sealed class SpaceApiFactory : ApiFactory
{
    public const int MaxInstances = 2;

    public SpaceManager Spaces => Services.GetRequiredService<SpaceManager>();

    public SpaceTicker Ticker => Services.GetServices<IHostedService>().OfType<SpaceTicker>().Single();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Spaces:TickerEnabled", "false");
        builder.UseSetting("Spaces:MaxInstances", MaxInstances.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Spaces:MaxConnectionsPerAddress", "100");
        builder.UseSetting("Spaces:TicketsPerMinute", "1000");
    }

    /// <summary>Ends reservations and empty instances left by earlier tests.</summary>
    public void Settle()
    {
        Clock.Advance(TimeSpan.FromMinutes(2));
        Spaces.Tick();
        Clock.Advance(TimeSpan.FromMinutes(2));
        Spaces.Tick();
    }

    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Clock.Advance(SpaceRules.TickInterval);
            Spaces.Tick();
        }
    }
}

/// <summary>A hub client over the test server's WebSockets, MessagePack like the web client, recording every event.</summary>
public sealed class HubPlayer : IAsyncDisposable
{
    private readonly ConcurrentQueue<(string Method, object?[] Args)> _events = new();
    private readonly SemaphoreSlim _signal = new(0);

    private HubPlayer(HubConnection connection)
    {
        Connection = connection;
        Record<uint, int[]>(SpaceEvents.Snapshot);
        Record<SpaceMember>(SpaceEvents.Joined);
        Record<int>(SpaceEvents.Left);
        Record<int, SpaceLook>(SpaceEvents.Look);
        Record<int, string>(SpaceEvents.Emote);
        Record<int, string>(SpaceEvents.QuickChat);
        Record<int, string>(SpaceEvents.Sticker);
        Record<SpaceWelcome>(SpaceEvents.Moved);
        Record<string>(SpaceEvents.Error);
        Record<string>(SpaceEvents.Kicked);
    }

    public HubConnection Connection { get; }

    public IEnumerable<(string Method, object?[] Args)> Events => _events;

    public static async Task<string> TicketAsync(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var body = await Contract.AssertAsync(await player.Http.PostAsync("/api/v1/spaces/tickets", null), HttpStatusCode.Created, "SpaceTicket");
        return body.GetProperty("ticket").GetString()!;
    }

    public static async Task<HubPlayer> ConnectAsync(ApiFactory api, Player player) =>
        await ConnectWithTicketAsync(api, await TicketAsync(player));

    public static async Task<HubPlayer> ConnectWithTicketAsync(
        ApiFactory api, string? ticket, string? origin = null, bool json = false)
    {
        ArgumentNullException.ThrowIfNull(api);
        var server = api.Server;
        var url = new Uri($"https://localhost{SpaceHub.Path}" + (ticket is null ? string.Empty : $"?access_token={Uri.EscapeDataString(ticket)}"));
        var builder = new HubConnectionBuilder().WithUrl(url, HttpTransportType.WebSockets, options =>
        {
            options.SkipNegotiation = true;
            options.WebSocketFactory = async (context, cancellationToken) =>
            {
                var client = server.CreateWebSocketClient();
                if (origin is not null)
                {
                    client.ConfigureRequest = request => request.Headers.Origin = origin;
                }

                return await client.ConnectAsync(context.Uri, cancellationToken);
            };
        });
        if (!json)
        {
            builder.AddMessagePackProtocol();
        }

        var connection = builder.Build();
        var spaceClient = new HubPlayer(connection);
        await connection.StartAsync();
        return spaceClient;
    }

    public static async Task<(HubPlayer Client, SpaceWelcome Welcome)> JoinAsync(ApiFactory api, Player player)
    {
        var client = await ConnectAsync(api, player);
        return (client, await client.Connection.InvokeAsync<SpaceWelcome>("Join"));
    }

    /// <summary>Waits for the first event of <paramref name="method"/> that matches, and returns its arguments.</summary>
    public async Task<object?[]> WaitForAsync(string method, Func<object?[], bool>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            if (_events.FirstOrDefault(e => e.Method == method && (match is null || match(e.Args))) is { Method: not null } found)
            {
                return found.Args;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                Assert.Fail($"no matching '{method}' event; got: {string.Join(", ", _events.Select(Describe))}");
            }

            await _signal.WaitAsync(left < TimeSpan.FromMilliseconds(100) ? left : TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>The first snapshot entry of one slot, after a tick, that matches: (tick, x, z, heading, state).</summary>
    public async Task<(uint Tick, int X, int Z, int Heading, int State)> AvatarAsync(
        int slot, Func<(int X, int Z, int Heading, int State), bool>? match = null, uint afterTick = 0)
    {
        (uint, int, int, int, int)? found = null;
        await WaitForAsync(SpaceEvents.Snapshot, args =>
        {
            var tick = (uint)args[0]!;
            var avatars = (int[])args[1]!;
            for (var i = 0; tick > afterTick && i < avatars.Length; i += 5)
            {
                var entry = (avatars[i + 1], avatars[i + 2], avatars[i + 3], avatars[i + 4]);
                if (avatars[i] == slot && (match is null || match(entry)))
                {
                    found = (tick, entry.Item1, entry.Item2, entry.Item3, entry.Item4);
                    return true;
                }
            }

            return false;
        });
        return found!.Value;
    }

    public static bool Has(int[] avatars, int slot)
    {
        for (var i = 0; i < avatars.Length; i += 5)
        {
            if (avatars[i] == slot)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe((string Method, object?[] Args) e) =>
        e.Method + "(" + string.Join(" ", e.Args.Select(a => a is int[] values ? "[" + string.Join(",", values) + "]" : a?.ToString())) + ")";

    public void Clear() => _events.Clear();

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        _signal.Dispose();
    }

    private void Record<T1>(string method) =>
        Connection.On<T1>(method, a => Add(method, [a]));

    private void Record<T1, T2>(string method) =>
        Connection.On<T1, T2>(method, (a, b) => Add(method, [a, b]));

    private void Add(string method, object?[] args)
    {
        _events.Enqueue((method, args));
        _signal.Release();
    }
}
