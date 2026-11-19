using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

/// <summary>
/// The edge of the hub (protocol doc §2, §7), before authentication: only a WebSocket upgrade on the exact hub path
/// (no negotiate, no long polling), a same-origin <c>Origin</c> when the browser sends one, and the connection caps.
/// The request stays in this middleware for the whole life of the WebSocket, so the caps are released in
/// <c>finally</c>.
/// </summary>
public sealed class SpaceGate(RequestDelegate next, SpaceManager spaces, IConfiguration configuration, IHostEnvironment environment)
{
    public const string AddressItem = "vybe.spaces.address";

    private readonly string[] _origins = EdgeSecurity.CorsOrigins(configuration, environment);

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments(SpaceHub.Path, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!string.Equals(request.Path.Value?.TrimEnd('/'), SpaceHub.Path, StringComparison.OrdinalIgnoreCase))
        {
            await ProblemWriter.WriteAsync(context, 404, "not_found"); // negotiate and other SignalR sub-paths
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await ProblemWriter.WriteAsync(context, 400, "malformed_request");
            return;
        }

        if (request.Headers.Origin is { Count: > 0 } origin && !IsAllowedOrigin(request, origin.ToString()))
        {
            await ProblemWriter.WriteAsync(context, 403, "forbidden");
            return;
        }

        var address = ClientAddress.RateLimitPartition(context.Connection.RemoteIpAddress);
        switch (spaces.TryAdmit(address))
        {
            case SpaceManager.Admission.ServerFull:
                await ProblemWriter.WriteAsync(context, 503, "service_unavailable");
                return;
            case SpaceManager.Admission.PerAddressLimit:
                await ProblemWriter.WriteAsync(context, 429, "rate_limited");
                return;
        }

        try
        {
            context.Items[AddressItem] = address;
            await next(context);
        }
        finally
        {
            spaces.Release(address);
        }
    }

    private bool IsAllowedOrigin(HttpRequest request, string origin) =>
        string.Equals(origin, $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase)
        || _origins.Contains(origin, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Service and endpoint set-up for real-time spaces.</summary>
public static class SpaceSetup
{
    public static IServiceCollection AddVybeSpaces(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(SpaceOptions.Section).Get<SpaceOptions>() ?? new SpaceOptions();
        services.AddSingleton(options);
        services.AddSingleton(sp => new QuickChatManifest(sp.GetRequiredService<Content.ContentCatalog>().Options.Root));
        services.AddSingleton<SpaceTickets>();
        services.AddSingleton<SpaceMetrics>();
        services.AddSingleton<SpaceManager>();
        services.AddHostedService<SpaceTicker>();
        services.AddSignalR(hub =>
        {
            hub.SupportedProtocols = options.AllowJsonProtocol ? ["messagepack", "json"] : ["messagepack"];
            hub.MaximumReceiveMessageSize = SpaceRules.MaxReceiveMessageBytes;
            hub.MaximumParallelInvocationsPerClient = 1;
            hub.StreamBufferCapacity = 1;
            hub.EnableDetailedErrors = false;
            hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            hub.HandshakeTimeout = TimeSpan.FromSeconds(5);
        }).AddMessagePackProtocol();
        return services;
    }

    public static void MapVybeSpaces(this WebApplication app, RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(app);
        SpaceTicketEndpoints.Map(api);
        var spaces = app.Services.GetRequiredService<SpaceManager>();
        app.MapConnections(
                SpaceHub.Path,
                Dispatcher(),
                builder =>
                {
                    builder.Use(next => connection => MeterInboundAsync(connection, next, spaces));
                    builder.UseHub<SpaceHub>();
                })
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(SpaceTicketAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build());
    }

    private static HttpConnectionDispatcherOptions Dispatcher()
    {
        var dispatcher = new HttpConnectionDispatcherOptions
        {
            Transports = HttpTransportType.WebSockets,
            ApplicationMaxBufferSize = 8 * 1024,
            TransportMaxBufferSize = 64 * 1024,
        };
        dispatcher.WebSockets.CloseTimeout = TimeSpan.FromSeconds(2);
        return dispatcher;
    }

    /// <summary>
    /// The raw byte budget of a connection (protocol doc §7): it also counts frames that never reach a hub method
    /// (failed binding, unknown method, pings), so a flood closes the connection.
    /// </summary>
    private static Task MeterInboundAsync(ConnectionContext connection, ConnectionDelegate next, SpaceManager spaces)
    {
        var bucket = new TokenBucket(SpaceRules.InboundBytesPerSecond, SpaceRules.InboundByteBurst, DateTimeOffset.UtcNow);
        var flooded = false;
        connection.Transport = new DuplexPipe(
            new MeteredPipeReader(connection.Transport.Input, bytes =>
            {
                if (!flooded && !bucket.TryTake(DateTimeOffset.UtcNow, bytes))
                {
                    flooded = true;
                    spaces.Kick(connection.ConnectionId, KickReasons.RateLimited);
                    connection.Abort();
                }
            }),
            connection.Transport.Output);
        return next(connection);
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}

/// <summary>Counts the bytes the hub consumes from a connection.</summary>
internal sealed class MeteredPipeReader(PipeReader inner, Action<long> consumed) : PipeReader
{
    private ReadOnlySequence<byte> _buffer;

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await inner.ReadAsync(cancellationToken);
        _buffer = result.Buffer;
        return result;
    }

    public override bool TryRead(out ReadResult result)
    {
        if (!inner.TryRead(out result))
        {
            return false;
        }

        _buffer = result.Buffer;
        return true;
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        Count(consumed);
        inner.AdvanceTo(consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        Count(consumed);
        inner.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    private void Count(SequencePosition position)
    {
        var bytes = _buffer.Slice(_buffer.Start, position).Length;
        _buffer = default;
        if (bytes > 0)
        {
            consumed(bytes);
        }
    }
}

/// <summary>
/// The 10 Hz tick loop, the account check every 30 s (protocol doc §8), and the graceful shutdown on deploy.
/// </summary>
public sealed partial class SpaceTicker(
    SpaceManager spaces, SpaceOptions options, IServiceScopeFactory scopes, TimeProvider clock, ILogger<SpaceTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.TickerEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(SpaceRules.TickInterval, clock);
        var nextCheck = clock.GetUtcNow() + SpaceRules.StatusCheckInterval;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    spaces.Tick();
                    if (clock.GetUtcNow() >= nextCheck)
                    {
                        nextCheck = clock.GetUtcNow() + SpaceRules.StatusCheckInterval;
                        await CheckAccountsAsync(stoppingToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogTickFailed(exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await spaces.ShutdownAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    /// <summary>Kicks connected players who became suspended, <c>deleting</c> or deleted (one query for all).</summary>
    public async Task CheckAccountsAsync(CancellationToken cancellationToken)
    {
        var players = spaces.Players();
        if (players.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VybeDbContext>();
        var now = clock.UtcNowForDb();
        var rows = await db.Players.AsNoTracking()
            .Where(p => players.Contains(p.Id))
            .Select(p => new { p.Id, p.Status, p.SuspendedUntil })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        foreach (var playerId in players)
        {
            if (!rows.TryGetValue(playerId, out var row) || row.Status is PlayerStatus.Deleting or PlayerStatus.Deleted)
            {
                spaces.KickPlayer(playerId, KickReasons.AccountUnavailable);
            }
            else if (row.Status == PlayerStatus.Suspended && (row.SuspendedUntil is null || row.SuspendedUntil > now))
            {
                spaces.KickPlayer(playerId, KickReasons.Suspended);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Space tick failed")]
    private partial void LogTickFailed(Exception exception);
}
