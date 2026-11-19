using System.Text.Json;

namespace Vybe.Api.Features;

/// <summary>Real-time spaces settings (docs/design/p2-spaces-protocol.md). Defaults are the provisional values of the doc.</summary>
public sealed class SpaceOptions
{
    public const string Section = "Spaces";

    /// <summary>Test configurations only: also accept the SignalR JSON protocol. Production speaks MessagePack only.</summary>
    public bool AllowJsonProtocol { get; init; }

    /// <summary>False in tests: the tests call <see cref="SpaceManager.Tick"/> themselves.</summary>
    public bool TickerEnabled { get; init; } = true;

    public int MaxInstances { get; init; } = 64;

    public int MaxConnections { get; init; } = 1100;

    public int MaxConnectionsPerAddress { get; init; } = 20;

    public int TicketsPerMinute { get; init; } = 10;
}

/// <summary>The fixed rules of the protocol (§3, §6, §7 of the protocol doc).</summary>
public static class SpaceRules
{
    public const int ProtocolVersion = 1;
    public const int InstanceCapacity = 16;
    public const int TickHz = 10;
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    public const int BoundCm = 1600;
    public const int SpawnRadiusCm = 400;
    public const int SpeedCmPerS = 350;
    public const int MaxIntent = 127;
    public const int DeadZone = 16;
    public const int MaxIdLength = 40;
    public const int MaxReceiveMessageBytes = 1024;

    public static readonly TimeSpan IntentLifetime = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan Reservation = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan EmptyInstanceLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan JoinDeadline = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StatusCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Snapshot every tick while anything moves, else every 10th tick (1 Hz).</summary>
    public const int IdleSnapshotEvery = 10;

    public const double MessagesPerSecond = 15;
    public const double MessageBurst = 15;
    public static readonly TimeSpan ExpressionInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LookInterval = TimeSpan.FromSeconds(5);
    /// <summary>At most one <c>Report</c> or <c>Block</c> per second and connection (each is a database write).</summary>
    public static readonly TimeSpan SafetyActionInterval = TimeSpan.FromSeconds(1);
    public const int JoinsPerMinute = 5;
    public const int MaxDroppedPerMinute = 150;
    public const int MaxInvalidPerMinute = 20;

    /// <summary>Raw transport budget (all frames, also frames that fail binding): bytes per second and burst.</summary>
    public const double InboundBytesPerSecond = 2048;
    public const double InboundByteBurst = 4096;

    /// <summary>Queued messages per connection; a client that falls this far behind is closed.</summary>
    public const int OutboxCapacity = 128;
}

/// <summary><c>assets/manifests/quickchat.v1.json</c>: the only quick-chat phrase and sticker ids the hub accepts (P2-3).</summary>
public sealed class QuickChatManifest
{
    public QuickChatManifest(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "assets", "manifests", "quickchat.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        Version = root.GetProperty("version").GetInt32();
        Phrases = Ids(root, "phrases");
        Stickers = Ids(root, "stickers");
    }

    public int Version { get; }

    public IReadOnlySet<string> Phrases { get; }

    public IReadOnlySet<string> Stickers { get; }

    private static HashSet<string> Ids(JsonElement root, string property)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty(property).EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id) || id.Length > SpaceRules.MaxIdLength || !ids.Add(id))
            {
                throw new InvalidDataException($"quickchat.v1.json: {property} has an empty, long or repeated id");
            }
        }

        return ids;
    }
}
