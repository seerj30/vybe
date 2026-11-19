using System.Threading.Channels;
using MessagePack;
using Microsoft.AspNetCore.SignalR;

namespace Vybe.Api.Features;

// Wire types (protocol doc §5). MessagePack keys and JSON names are the same camelCase names.

[MessagePackObject]
public sealed record SpaceBounds(
    [property: Key("minX")] int MinX,
    [property: Key("maxX")] int MaxX,
    [property: Key("minZ")] int MinZ,
    [property: Key("maxZ")] int MaxZ);

[MessagePackObject]
public sealed record SpaceEquipped(
    [property: Key("hair")] string? Hair,
    [property: Key("top")] string? Top,
    [property: Key("bottom")] string? Bottom,
    [property: Key("shoes")] string? Shoes,
    [property: Key("emote")] string? Emote,
    [property: Key("accessories")] string?[] Accessories);

[MessagePackObject]
public sealed record SpaceLook(
    [property: Key("bodyPresetId")] string BodyPresetId,
    [property: Key("skinToneId")] string SkinToneId,
    [property: Key("facePresetId")] string FacePresetId,
    [property: Key("hairPresetId")] string HairPresetId,
    [property: Key("hairColorId")] string HairColorId,
    [property: Key("equipped")] SpaceEquipped Equipped);

/// <summary>
/// <c>key</c>: a number for this avatar's time in the instance (new on every placement, counted per instance). Slots
/// are reused, so <c>Report</c> and <c>Block</c> name the member by slot and key. It is not a player id.
/// </summary>
[MessagePackObject]
public sealed record SpaceMember(
    [property: Key("slot")] int Slot,
    [property: Key("key")] int MemberKey,
    [property: Key("handle")] string? Handle,
    [property: Key("look")] SpaceLook Look);

[MessagePackObject]
public sealed record SpaceWelcome(
    [property: Key("protocolVersion")] int ProtocolVersion,
    [property: Key("manifestVersion")] int ManifestVersion,
    [property: Key("instanceId")] int InstanceId,
    [property: Key("slot")] int Slot,
    [property: Key("tickHz")] int TickHz,
    [property: Key("bounds")] SpaceBounds Bounds,
    [property: Key("speedCmPerS")] int SpeedCmPerS,
    [property: Key("members")] IReadOnlyList<SpaceMember> Members);

/// <summary>Server → client handler names (protocol doc §5).</summary>
public static class SpaceEvents
{
    public const string Snapshot = "snapshot";
    public const string Joined = "joined";
    public const string Left = "left";
    public const string Look = "look";
    public const string Emote = "emote";
    public const string QuickChat = "quickChat";
    public const string Sticker = "sticker";
    public const string Moved = "moved";
    public const string Error = "error";
    public const string Kicked = "kicked";
}

/// <summary><c>HubException</c> messages of <c>Report</c> and <c>Block</c> (besides the error codes).</summary>
public static class SafetyErrors
{
    /// <summary>The member left the instance (or the slot is the caller's own).</summary>
    public const string NotFound = "not_found";
}

/// <summary>Kick reasons (protocol doc §8).</summary>
public static class KickReasons
{
    public const string ServerShutdown = "server_shutdown";
    public const string Replaced = "replaced";
    public const string Idle = "idle";
    public const string NotJoined = "not_joined";
    public const string RateLimited = "rate_limited";
    public const string InvalidInput = "invalid_input";
    public const string Suspended = "suspended";
    public const string AccountUnavailable = "account_unavailable";
    public const string SpacesFull = "spaces_full";
    public const string SlowClient = "slow_client";
}

/// <summary>
/// What the database says about a joining player (read before the state lock is taken). <c>Blocks</c>: both
/// directions (who may not share an instance); <c>BlockedBy</c>: players who blocked this one.
/// </summary>
public sealed record SpaceProfile(
    string? Handle, SpaceLook Look, IReadOnlySet<string> OwnedEmotes, IReadOnlySet<Guid> Blocks, IReadOnlySet<Guid> BlockedBy);

internal sealed record OutMessage(string Method, object?[] Args, bool IsSnapshot = false, string? KickReason = null);

/// <summary>A token bucket on the application clock.</summary>
internal sealed class TokenBucket
{
    private readonly double _perSecond;
    private readonly double _burst;
    private double _tokens;
    private DateTimeOffset _at;

    public TokenBucket(double perSecond, double burst, DateTimeOffset now)
    {
        (_perSecond, _burst, _tokens, _at) = (perSecond, burst, burst, now);
    }

    public bool TryTake(DateTimeOffset now, double amount = 1)
    {
        var elapsed = Math.Max(0, (now - _at).TotalSeconds);
        _tokens = Math.Min(_burst, _tokens + (elapsed * _perSecond));
        _at = now;
        if (_tokens < amount)
        {
            return false;
        }

        _tokens -= amount;
        return true;
    }
}

/// <summary>Events in a sliding 60 s window (dropped and invalid input counters, joins).</summary>
internal sealed class MinuteWindow
{
    private readonly Queue<DateTimeOffset> _times = new();

    public int Total { get; private set; }

    public int Add(DateTimeOffset now)
    {
        _times.Enqueue(now);
        Total++;
        return Count(now);
    }

    public int Count(DateTimeOffset now)
    {
        while (_times.Count > 0 && _times.Peek() <= now - TimeSpan.FromMinutes(1))
        {
            _times.Dequeue();
        }

        return _times.Count;
    }
}

/// <summary>
/// One hub connection. All fields except the outbox are guarded by the <see cref="SpaceManager"/> lock. The outbox
/// has one writer task per connection, so a slow client never blocks the tick loop: snapshots are skipped while one
/// is still queued, and a client with a full outbox is closed.
/// </summary>
internal sealed class SpaceClient
{
    private static readonly OutMessage SnapshotMarker = new(SpaceEvents.Snapshot, [], IsSnapshot: true);

    private readonly Channel<OutMessage> _outbox = Channel.CreateBounded<OutMessage>(
        new BoundedChannelOptions(SpaceRules.OutboxCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private OutMessage? _latestSnapshot;

    public SpaceClient(string connectionId, Guid playerId, string address, HubCallerContext context, DateTimeOffset now)
    {
        ConnectionId = connectionId;
        PlayerId = playerId;
        Address = address;
        Context = context;
        ConnectedAt = now;
        LastInputAt = now;
        Messages = new TokenBucket(SpaceRules.MessagesPerSecond, SpaceRules.MessageBurst, now);
    }

    public string ConnectionId { get; }

    public Guid PlayerId { get; }

    public string Address { get; }

    public HubCallerContext Context { get; }

    public DateTimeOffset ConnectedAt { get; }

    public DateTimeOffset LastInputAt { get; set; }

    public SpaceAvatar? Avatar { get; set; }

    public TokenBucket Messages { get; }

    public Dictionary<string, DateTimeOffset> LastExpression { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset LastLook { get; set; } = DateTimeOffset.MinValue;

    public DateTimeOffset LastSafetyAction { get; set; } = DateTimeOffset.MinValue;

    public MinuteWindow Dropped { get; } = new();

    public MinuteWindow Invalid { get; } = new();

    public MinuteWindow Joins { get; } = new();

    public Dictionary<string, DateTimeOffset> LastErrorSent { get; } = new(StringComparer.Ordinal);

    public string? ClosingReason { get; set; }

    public bool Joining { get; set; }

    public ChannelReader<OutMessage> Reader => _outbox.Reader;

    /// <summary>
    /// Queues a message; false when the outbox is full (the caller closes the client). Snapshots are full state, so a
    /// newer one replaces a snapshot that is still waiting: the queue holds one marker, the pump sends the latest.
    /// </summary>
    public bool TryEnqueue(OutMessage message)
    {
        if (!message.IsSnapshot)
        {
            return _outbox.Writer.TryWrite(message);
        }

        if (Interlocked.Exchange(ref _latestSnapshot, message) is not null)
        {
            return true; // a marker is already queued; it now sends this snapshot
        }

        if (_outbox.Writer.TryWrite(SnapshotMarker))
        {
            return true;
        }

        Interlocked.Exchange(ref _latestSnapshot, null);
        return false;
    }

    /// <summary>The message to send for a dequeued item (the latest snapshot for a marker), or null.</summary>
    public OutMessage? Resolve(OutMessage message) =>
        message == SnapshotMarker ? Interlocked.Exchange(ref _latestSnapshot, null) : message;

    public void Complete() => _outbox.Writer.TryComplete();
}

/// <summary>An avatar in an instance. Guarded by the <see cref="SpaceManager"/> lock.</summary>
internal sealed class SpaceAvatar(Guid playerId, SpaceInstance instance, int slot, int key, SpaceProfile profile)
{
    public Guid PlayerId { get; } = playerId;

    /// <summary>The member key (<see cref="SpaceMember"/>); a reconnect to the reserved slot keeps it.</summary>
    public int Key { get; } = key;

    public SpaceInstance Instance { get; set; } = instance;

    public int Slot { get; set; } = slot;

    public string? Handle { get; set; } = profile.Handle;

    public SpaceLook Look { get; set; } = profile.Look;

    public IReadOnlySet<string> OwnedEmotes { get; set; } = profile.OwnedEmotes;

    /// <summary>Players in a block with this one, both directions.</summary>
    public HashSet<Guid> Blocks { get; } = [.. profile.Blocks];

    /// <summary>Players who blocked this one.</summary>
    public HashSet<Guid> BlockedBy { get; } = [.. profile.BlockedBy];

    public double X { get; set; }

    public double Z { get; set; }

    public int Heading { get; set; }

    public int State { get; set; }

    public int IntentX { get; set; }

    public int IntentZ { get; set; }

    public DateTimeOffset IntentAt { get; set; }

    /// <summary>The connection, or null while the slot is reserved after a dropped connection.</summary>
    public SpaceClient? Client { get; set; }

    public DateTimeOffset AwaySince { get; set; }

    public SpaceMember Member() => new(Slot, Key, Handle, Look);
}

internal sealed class SpaceInstance(int id, DateTimeOffset now)
{
    public int Id { get; } = id;

    public SpaceAvatar?[] Slots { get; } = new SpaceAvatar?[SpaceRules.InstanceCapacity];

    public DateTimeOffset EmptySince { get; set; } = now;

    /// <summary>The key of the next placed avatar (<see cref="SpaceMember"/>).</summary>
    public int NextKey { get; set; } = 1;

    public bool Changed { get; set; } = true;

    public int Count => Slots.Count(a => a is not null);

    public IEnumerable<SpaceAvatar> Avatars => Slots.OfType<SpaceAvatar>();

    public IEnumerable<SpaceClient> Clients => Avatars.Select(a => a.Client).OfType<SpaceClient>();
}

/// <summary>States in a snapshot (protocol doc §5).</summary>
internal static class AvatarStates
{
    public const int Idle = 0;
    public const int Walking = 1;
    public const int Away = 2;
}
