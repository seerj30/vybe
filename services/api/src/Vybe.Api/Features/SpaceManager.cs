using Microsoft.AspNetCore.SignalR;

namespace Vybe.Api.Features;

/// <summary>
/// All real-time space state (protocol doc §3–§9), in memory, in one API process (P2-2). One lock guards the state;
/// every operation under it is short and does no I/O. Sends go through each client's outbox, so the lock and the tick
/// loop never wait for a network write.
/// </summary>
public sealed partial class SpaceManager(
    SpaceOptions options,
    QuickChatManifest manifest,
    TimeProvider clock,
    IHubContext<SpaceHub> hub,
    SpaceMetrics metrics,
    ILogger<SpaceManager> logger)
{
    private static readonly TimeSpan LiveBlockLifetime = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SpaceClient> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, SpaceClient> _byPlayer = [];
    private readonly Dictionary<Guid, SpaceAvatar> _reserved = [];
    private readonly SortedDictionary<int, SpaceInstance> _instances = [];
    private readonly Dictionary<string, int> _perAddress = new(StringComparer.Ordinal);
    private readonly List<(Guid A, Guid B, DateTimeOffset At)> _liveBlocks = [];
    private readonly List<Task> _pumps = [];
    private int _connections;
    private int _nextInstanceId = 1;
    private uint _tick;
    private bool _stopping;

    public enum Admission { Accepted, PerAddressLimit, ServerFull }

    // ------------------------------------------------------------------ connections

    /// <summary>Reserves a connection before the WebSocket upgrade (protocol doc §7 caps).</summary>
    public Admission TryAdmit(string address)
    {
        lock (_gate)
        {
            if (_stopping || _connections >= options.MaxConnections)
            {
                return Admission.ServerFull;
            }

            var count = _perAddress.GetValueOrDefault(address);
            if (count >= options.MaxConnectionsPerAddress)
            {
                return Admission.PerAddressLimit;
            }

            _perAddress[address] = count + 1;
            _connections++;
            return Admission.Accepted;
        }
    }

    public void Release(string address)
    {
        lock (_gate)
        {
            _connections--;
            if (--_perAddress[address] == 0)
            {
                _perAddress.Remove(address);
            }
        }
    }

    internal void Connected(HubCallerContext context, Guid playerId, string address)
    {
        var now = clock.GetUtcNow();
        var client = new SpaceClient(context.ConnectionId, playerId, address, context, now);
        lock (_gate)
        {
            if (_stopping)
            {
                context.Abort();
                return;
            }

            if (_byPlayer.TryGetValue(playerId, out var previous))
            {
                KickLocked(previous, KickReasons.Replaced, now);
            }

            _clients[client.ConnectionId] = client;
            _byPlayer[playerId] = client;
            metrics.Connections = _clients.Count;
            _pumps.RemoveAll(p => p.IsCompleted);
            _pumps.Add(Task.Run(() => PumpAsync(client)));
        }
    }

    /// <summary>A dropped connection keeps its avatar 30 s as "away" (protocol doc §8).</summary>
    internal void Disconnected(string connectionId)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_clients.Remove(connectionId, out var client))
            {
                return; // kicked: already handled
            }

            if (_byPlayer.TryGetValue(client.PlayerId, out var current) && current == client)
            {
                _byPlayer.Remove(client.PlayerId);
            }

            if (client.Avatar is { } avatar)
            {
                Reserve(avatar, now);
            }

            LogClosed(client, "disconnected", now);
            client.Complete();
            metrics.Connections = _clients.Count;
        }
    }

    /// <summary>Closes a connection with <c>kicked</c> (transport limits, account checks).</summary>
    public void Kick(string connectionId, string reason)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(connectionId, out var client))
            {
                KickLocked(client, reason, clock.GetUtcNow());
            }
        }
    }

    /// <summary>Players with a connection or a reserved slot (account checks, protocol doc §8).</summary>
    public IReadOnlyList<Guid> Players()
    {
        lock (_gate)
        {
            return [.. _byPlayer.Keys.Concat(_reserved.Keys).Distinct()];
        }
    }

    public void KickPlayer(Guid playerId, string reason)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_byPlayer.TryGetValue(playerId, out var client))
            {
                KickLocked(client, reason, now);
            }

            if (_reserved.TryGetValue(playerId, out var avatar))
            {
                RemoveAvatar(avatar, now);
            }
        }
    }

    // ------------------------------------------------------------------ join and leave

    /// <summary>First half of <c>Join</c>: limits, before the profile is read from the database.</summary>
    internal Guid BeginJoin(string connectionId)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var client = Client(connectionId);
            if (!Admit(client, now, requireJoined: false, "join"))
            {
                throw new HubException(KickReasons.RateLimited);
            }

            if (client.Avatar is not null || client.Joining)
            {
                throw new HubException("already_joined");
            }

            if (client.Joins.Count(now) >= SpaceRules.JoinsPerMinute)
            {
                Drop(client, now);
                throw new HubException(KickReasons.RateLimited);
            }

            client.Joins.Add(now);
            client.Joining = true;
            return client.PlayerId;
        }
    }

    internal void CancelJoin(string connectionId)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(connectionId, out var client))
            {
                client.Joining = false;
            }
        }
    }

    /// <summary>Second half of <c>Join</c>: a reserved slot, else the fullest instance without a block, else a new one.</summary>
    internal SpaceWelcome CompleteJoin(string connectionId, SpaceProfile profile)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var client = Client(connectionId);
            client.Joining = false;
            profile = WithLiveBlocks(client.PlayerId, profile, now);
            var blocks = profile.Blocks;
            client.LastInputAt = now;

            if (_reserved.Remove(client.PlayerId, out var reserved))
            {
                if (!Conflicts(reserved.Instance, client.PlayerId, blocks))
                {
                    Reclaim(client, reserved, profile);
                    LogJoined(client.PlayerId, reserved.Instance.Id);
                    return Welcome(reserved);
                }

                RemoveAvatar(reserved, now);
            }

            var avatar = Place(client, profile, now, exclude: null) ?? throw new HubException(KickReasons.SpacesFull);
            LogJoined(client.PlayerId, avatar.Instance.Id);
            return Welcome(avatar);
        }
    }

    internal void Leave(string connectionId)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var client = Client(connectionId);
            if (Admit(client, now, requireJoined: true, "leave"))
            {
                RemoveAvatar(client.Avatar!, now);
                client.Avatar = null;
                client.LastInputAt = now;
            }
        }
    }

    // ------------------------------------------------------------------ input

    internal void Move(string connectionId, int dx, int dz)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var client = Client(connectionId);
            if (!Admit(client, now, requireJoined: true, "move"))
            {
                return;
            }

            if (dx is < -SpaceRules.MaxIntent or > SpaceRules.MaxIntent || dz is < -SpaceRules.MaxIntent or > SpaceRules.MaxIntent)
            {
                Invalid(client, now, "invalid_input");
                return;
            }

            var avatar = client.Avatar!;
            var stop = (dx * dx) + (dz * dz) < SpaceRules.DeadZone * SpaceRules.DeadZone;
            avatar.IntentX = stop ? 0 : dx;
            avatar.IntentZ = stop ? 0 : dz;
            avatar.IntentAt = now;
            client.LastInputAt = now;
        }
    }

    internal void Emote(string connectionId, string? sku) =>
        Expression(connectionId, "emote", sku, SpaceEvents.Emote, (avatar, id) => avatar.OwnedEmotes.Contains(id));

    internal void QuickChat(string connectionId, string? id) =>
        Expression(connectionId, "quick_chat", id, SpaceEvents.QuickChat, (_, value) => manifest.Phrases.Contains(value));

    internal void Sticker(string connectionId, string? id) =>
        Expression(connectionId, "sticker", id, SpaceEvents.Sticker, (_, value) => manifest.Stickers.Contains(value));

    /// <summary>First half of <c>RefreshLook</c>: limits; null when the call is dropped.</summary>
    internal Guid? BeginRefreshLook(string connectionId)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var client = Client(connectionId);
            if (!Admit(client, now, requireJoined: true, "refresh_look"))
            {
                return null;
            }

            if (now - client.LastLook < SpaceRules.LookInterval)
            {
                Drop(client, now);
                return null;
            }

            client.LastLook = now;
            return client.PlayerId;
        }
    }

    internal void CompleteRefreshLook(string connectionId, SpaceProfile profile)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(connectionId, out var client) || client.Avatar is not { } avatar)
            {
                return;
            }

            var handleChanged = avatar.Handle != profile.Handle; // a privacy change shows at once
            avatar.Handle = profile.Handle;
            avatar.Look = profile.Look;
            avatar.OwnedEmotes = profile.OwnedEmotes;
            if (handleChanged)
            {
                Broadcast(avatar.Instance, SpaceEvents.Joined, [avatar.Member()]);
            }
            else
            {
                Broadcast(avatar.Instance, SpaceEvents.Look, [avatar.Slot, avatar.Look]);
            }
        }
    }

    private void Expression(string connectionId, string type, string? id, string handler, Func<SpaceAvatar, string, bool> allowed)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var client = Client(connectionId);
            if (!Admit(client, now, requireJoined: true, type))
            {
                return;
            }

            var avatar = client.Avatar!;
            if (id is null || id.Length is 0 or > SpaceRules.MaxIdLength || !allowed(avatar, id))
            {
                Invalid(client, now, "invalid_input");
                return;
            }

            if (client.LastExpression.TryGetValue(type, out var last) && now - last < SpaceRules.ExpressionInterval)
            {
                Drop(client, now);
                return;
            }

            client.LastExpression[type] = now;
            client.LastInputAt = now;
            Broadcast(avatar.Instance, handler, [avatar.Slot, id]);
        }
    }

    // ------------------------------------------------------------------ blocks

    /// <summary>
    /// Called after a block commits (protocol doc §9): the pair never shares an instance. The blocker moves; the
    /// blocked player sees a normal <c>left</c>.
    /// </summary>
    public void ApplyBlock(Guid blocker, Guid blocked)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            _liveBlocks.RemoveAll(b => now - b.At > LiveBlockLifetime);
            _liveBlocks.Add((blocker, blocked, now));
            var a = AvatarOf(blocker);
            var b = AvatarOf(blocked);
            a?.Blocks.Add(blocked);
            b?.Blocks.Add(blocker);
            b?.BlockedBy.Add(blocker);
            if (a is null || b is null || a.Instance != b.Instance)
            {
                return;
            }

            if (a.Client is not { } client)
            {
                RemoveAvatar(a, now); // the blocker's reserved slot goes away
                return;
            }

            var from = a.Instance;
            RemoveAvatar(a, now);
            client.Avatar = null;
            var profile = new SpaceProfile(a.Handle, a.Look, a.OwnedEmotes, a.Blocks, a.BlockedBy);
            if (Place(client, profile, now, exclude: from) is { } moved)
            {
                client.TryEnqueue(new OutMessage(SpaceEvents.Moved, [Welcome(moved)]));
                metrics.MessagesOut(SpaceEvents.Moved, 1);
            }
            else
            {
                KickLocked(client, KickReasons.SpacesFull, now);
            }
        }
    }

    // ------------------------------------------------------------------ tick

    /// <summary>One server tick (100 ms): movement, snapshots, reservations, timeouts, empty instances.</summary>
    public void Tick()
    {
        var started = clock.GetTimestamp();
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            _tick++;
            var heartbeat = _tick % SpaceRules.IdleSnapshotEvery == 0;
            foreach (var instance in _instances.Values.ToList())
            {
                foreach (var avatar in instance.Avatars.ToList())
                {
                    if (avatar.Client is null && now - avatar.AwaySince >= SpaceRules.Reservation)
                    {
                        RemoveAvatar(avatar, now);
                        continue;
                    }

                    instance.Changed |= Step(avatar, now);
                }

                if (instance.Count == 0)
                {
                    if (now - instance.EmptySince >= SpaceRules.EmptyInstanceLifetime)
                    {
                        _instances.Remove(instance.Id);
                    }

                    continue;
                }

                if (instance.Changed || heartbeat)
                {
                    instance.Changed = false;
                    SendSnapshot(instance);
                }
            }

            foreach (var client in _clients.Values.ToList())
            {
                if (client.Avatar is null && !client.Joining && now - client.LastInputAt >= SpaceRules.JoinDeadline)
                {
                    KickLocked(client, KickReasons.NotJoined, now);
                }
                else if (now - client.LastInputAt >= SpaceRules.IdleTimeout)
                {
                    KickLocked(client, KickReasons.Idle, now);
                }
            }

            metrics.Instances = _instances.Count;
            metrics.Avatars = _instances.Values.Sum(i => i.Count);
        }

        metrics.Tick(clock.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Applies the latest intent for 100 ms; true when the snapshot changes.</summary>
    private static bool Step(SpaceAvatar avatar, DateTimeOffset now)
    {
        var state = AvatarStates.Away;
        var (x, z, heading) = (avatar.X, avatar.Z, avatar.Heading);
        if (avatar.Client is not null)
        {
            var active = (avatar.IntentX != 0 || avatar.IntentZ != 0) && now - avatar.IntentAt <= SpaceRules.IntentLifetime;
            state = active ? AvatarStates.Walking : AvatarStates.Idle;
            if (active)
            {
                var length = Math.Sqrt((avatar.IntentX * avatar.IntentX) + (avatar.IntentZ * avatar.IntentZ));
                var step = SpaceRules.SpeedCmPerS * Math.Min(length / SpaceRules.MaxIntent, 1) * SpaceRules.TickInterval.TotalSeconds;
                var (nx, nz) = (avatar.IntentX / length, avatar.IntentZ / length);
                x = Math.Clamp(x + (nx * step), -SpaceRules.BoundCm, SpaceRules.BoundCm);
                z = Math.Clamp(z + (nz * step), -SpaceRules.BoundCm, SpaceRules.BoundCm);
                heading = HeadingOf(nx, nz);
            }
        }

        var changed = state != avatar.State || heading != avatar.Heading
            || (int)Math.Round(x) != (int)Math.Round(avatar.X) || (int)Math.Round(z) != (int)Math.Round(avatar.Z);
        (avatar.X, avatar.Z, avatar.Heading, avatar.State) = (x, z, heading, state);
        return changed;
    }

    /// <summary>Heading in 1/256 turns: θ = h × 2π / 256, facing (sin θ, cos θ).</summary>
    public static int HeadingOf(double x, double z)
    {
        var turns = Math.Atan2(x, z) / (2 * Math.PI);
        return (int)Math.Round(turns * 256) & 0xFF;
    }

    private void SendSnapshot(SpaceInstance instance)
    {
        var avatars = new int[instance.Count * 5];
        var i = 0;
        foreach (var avatar in instance.Avatars)
        {
            avatars[i++] = avatar.Slot;
            avatars[i++] = (int)Math.Round(avatar.X);
            avatars[i++] = (int)Math.Round(avatar.Z);
            avatars[i++] = avatar.Heading;
            avatars[i++] = avatar.State;
        }

        var message = new OutMessage(SpaceEvents.Snapshot, [_tick, avatars], IsSnapshot: true);
        var sent = 0;
        foreach (var client in instance.Clients.ToList())
        {
            if (client.TryEnqueue(message))
            {
                sent++;
            }
            else
            {
                KickLocked(client, KickReasons.SlowClient, clock.GetUtcNow());
            }
        }

        metrics.MessagesOut(SpaceEvents.Snapshot, sent);
    }

    // ------------------------------------------------------------------ shutdown

    /// <summary>Deploy (protocol doc §8): refuse new connections, kick everyone with <c>server_shutdown</c>, wait up to 2 s.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Task[] pumps;
        lock (_gate)
        {
            _stopping = true;
            foreach (var client in _clients.Values.ToList())
            {
                client.Avatar = null; // no left/joined traffic to clients that are all closing
                KickLocked(client, KickReasons.ServerShutdown, clock.GetUtcNow());
            }

            _instances.Clear();
            _reserved.Clear();
            pumps = [.. _pumps];
        }

        try
        {
            await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            LogShutdownTimeout();
        }
    }

    // ------------------------------------------------------------------ helpers (call under the lock)

    private SpaceClient Client(string connectionId) =>
        _clients.TryGetValue(connectionId, out var client) ? client : throw new HubException("not_connected");

    /// <summary>The per-connection message budget and the joined check; counts the message type.</summary>
    private bool Admit(SpaceClient client, DateTimeOffset now, bool requireJoined, string type)
    {
        metrics.MessageIn(type);
        if (!client.Messages.TryTake(now))
        {
            Drop(client, now);
            return false;
        }

        if (requireJoined && client.Avatar is null)
        {
            Invalid(client, now, "not_joined");
            return false;
        }

        return true;
    }

    private void Drop(SpaceClient client, DateTimeOffset now)
    {
        metrics.Rejected(KickReasons.RateLimited);
        if (client.Dropped.Add(now) > SpaceRules.MaxDroppedPerMinute)
        {
            KickLocked(client, KickReasons.RateLimited, now);
            return;
        }

        SendError(client, KickReasons.RateLimited, now);
    }

    private void Invalid(SpaceClient client, DateTimeOffset now, string code)
    {
        metrics.Rejected(code);
        if (client.Invalid.Add(now) > SpaceRules.MaxInvalidPerMinute)
        {
            KickLocked(client, KickReasons.InvalidInput, now);
            return;
        }

        SendError(client, code, now);
    }

    private void SendError(SpaceClient client, string code, DateTimeOffset now)
    {
        if (client.LastErrorSent.TryGetValue(code, out var last) && now - last < TimeSpan.FromSeconds(1))
        {
            return;
        }

        client.LastErrorSent[code] = now;
        if (!client.TryEnqueue(new OutMessage(SpaceEvents.Error, [code])))
        {
            KickLocked(client, KickReasons.SlowClient, now);
        }
    }

    /// <summary>
    /// Closes a client: <c>kicked</c> goes out through the outbox, then the connection is aborted (at the latest after
    /// 2 s). A replaced connection keeps its avatar as a reserved slot for the new connection.
    /// </summary>
    private void KickLocked(SpaceClient client, string reason, DateTimeOffset now)
    {
        if (client.ClosingReason is not null)
        {
            return;
        }

        client.ClosingReason = reason;
        _clients.Remove(client.ConnectionId);
        if (_byPlayer.TryGetValue(client.PlayerId, out var current) && current == client)
        {
            _byPlayer.Remove(client.PlayerId);
        }

        if (client.Avatar is { } avatar)
        {
            if (reason == KickReasons.Replaced)
            {
                Reserve(avatar, now);
            }
            else
            {
                RemoveAvatar(avatar, now);
            }

            client.Avatar = null;
        }

        metrics.Kick(reason);
        metrics.Connections = _clients.Count;
        LogClosed(client, reason, now);
        if (!client.TryEnqueue(new OutMessage(SpaceEvents.Kicked, [reason], KickReason: reason)))
        {
            client.Context.Abort();
        }

        client.Complete();
        _ = AbortLaterAsync(client);
    }

    private static async Task AbortLaterAsync(SpaceClient client)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        client.Context.Abort();
    }

    private void Reserve(SpaceAvatar avatar, DateTimeOffset now)
    {
        avatar.Client = null;
        avatar.AwaySince = now;
        avatar.IntentX = avatar.IntentZ = 0;
        avatar.Instance.Changed = true;
        _reserved[avatar.PlayerId] = avatar;
    }

    private void Reclaim(SpaceClient client, SpaceAvatar avatar, SpaceProfile profile)
    {
        avatar.Client = client;
        avatar.Handle = profile.Handle;
        avatar.Look = profile.Look;
        avatar.OwnedEmotes = profile.OwnedEmotes;
        avatar.Blocks.UnionWith(profile.Blocks);
        avatar.BlockedBy.UnionWith(profile.BlockedBy);
        avatar.Instance.Changed = true;
        client.Avatar = avatar;
        // A joined for an occupied slot replaces the member (handle or look may have changed while away).
        Broadcast(avatar.Instance, SpaceEvents.Joined, [avatar.Member()], except: client);
    }

    private SpaceAvatar? Place(SpaceClient client, SpaceProfile profile, DateTimeOffset now, SpaceInstance? exclude)
    {
        var instance = _instances.Values
            .Where(i => i != exclude && i.Count < SpaceRules.InstanceCapacity && !Conflicts(i, client.PlayerId, profile.Blocks))
            .OrderByDescending(i => i.Count)
            .ThenBy(i => i.Id)
            .FirstOrDefault();
        if (instance is null)
        {
            // The cap is about capacity. When instances with free slots exist and only blocks that other players placed on
            // this player keep them out, a new instance opens past the cap, up to twice the cap: else blocks from many
            // accounts could keep one player out of every plaza. The joiner's own blocks never open extra instances.
            var blockedByOthers = _instances.Values.Any(i => i != exclude && i.Count < SpaceRules.InstanceCapacity
                && i.Avatars.Any(a => profile.BlockedBy.Contains(a.PlayerId)));
            var limit = blockedByOthers ? options.MaxInstances * 2 : options.MaxInstances;
            if (_instances.Count >= limit)
            {
                return null;
            }

            instance = new SpaceInstance(_nextInstanceId++, now);
            _instances[instance.Id] = instance;
        }

        var slot = Array.IndexOf(instance.Slots, null);
        var avatar = new SpaceAvatar(client.PlayerId, instance, slot, profile) { Client = client, IntentAt = now };
        var angle = Random.Shared.NextDouble() * 2 * Math.PI;
        var radius = Math.Sqrt(Random.Shared.NextDouble()) * SpaceRules.SpawnRadiusCm;
        (avatar.X, avatar.Z) = (Math.Round(radius * Math.Sin(angle)), Math.Round(radius * Math.Cos(angle)));
        instance.Slots[slot] = avatar;
        instance.Changed = true;
        client.Avatar = avatar;
        Broadcast(instance, SpaceEvents.Joined, [avatar.Member()], except: client);
        return avatar;
    }

    private void RemoveAvatar(SpaceAvatar avatar, DateTimeOffset now)
    {
        var instance = avatar.Instance;
        if (instance.Slots[avatar.Slot] != avatar)
        {
            return;
        }

        instance.Slots[avatar.Slot] = null;
        instance.Changed = true;
        if (_reserved.TryGetValue(avatar.PlayerId, out var reserved) && reserved == avatar)
        {
            _reserved.Remove(avatar.PlayerId);
        }

        if (instance.Count == 0)
        {
            instance.EmptySince = now;
        }

        Broadcast(instance, SpaceEvents.Left, [avatar.Slot]);
    }

    private static bool Conflicts(SpaceInstance instance, Guid playerId, IReadOnlySet<Guid> blocks) =>
        instance.Avatars.Any(a => a.PlayerId != playerId && (blocks.Contains(a.PlayerId) || a.Blocks.Contains(playerId)));

    /// <summary>Adds blocks that committed after the joiner's profile was read (a block during <c>Join</c>).</summary>
    private SpaceProfile WithLiveBlocks(Guid playerId, SpaceProfile profile, DateTimeOffset now)
    {
        var blocks = new HashSet<Guid>(profile.Blocks);
        var blockedBy = new HashSet<Guid>(profile.BlockedBy);
        foreach (var (blocker, blocked, at) in _liveBlocks)
        {
            if (now - at > LiveBlockLifetime)
            {
                continue;
            }

            if (blocker == playerId)
            {
                blocks.Add(blocked);
            }
            else if (blocked == playerId)
            {
                blocks.Add(blocker);
                blockedBy.Add(blocker);
            }
        }

        return profile with { Blocks = blocks, BlockedBy = blockedBy };
    }

    private SpaceAvatar? AvatarOf(Guid playerId) =>
        _byPlayer.TryGetValue(playerId, out var client) && client.Avatar is { } avatar ? avatar : _reserved.GetValueOrDefault(playerId);

    private SpaceWelcome Welcome(SpaceAvatar avatar) => new(
        SpaceRules.ProtocolVersion,
        manifest.Version,
        avatar.Instance.Id,
        avatar.Slot,
        SpaceRules.TickHz,
        new SpaceBounds(-SpaceRules.BoundCm, SpaceRules.BoundCm, -SpaceRules.BoundCm, SpaceRules.BoundCm),
        SpaceRules.SpeedCmPerS,
        [.. avatar.Instance.Avatars.Select(a => a.Member())]);

    private void Broadcast(SpaceInstance instance, string handler, object?[] args, SpaceClient? except = null)
    {
        var message = new OutMessage(handler, args);
        var sent = 0;
        foreach (var client in instance.Clients.ToList())
        {
            if (client == except)
            {
                continue;
            }

            if (client.TryEnqueue(message))
            {
                sent++;
            }
            else
            {
                KickLocked(client, KickReasons.SlowClient, clock.GetUtcNow());
            }
        }

        metrics.MessagesOut(handler, sent);
    }

    /// <summary>One writer per connection: sends queued messages in order; after <c>kicked</c>, aborts the connection.</summary>
    private async Task PumpAsync(SpaceClient client)
    {
        var proxy = hub.Clients.Client(client.ConnectionId);
        try
        {
            await foreach (var item in client.Reader.ReadAllAsync())
            {
                if (client.Resolve(item) is not { } message)
                {
                    continue;
                }

                await proxy.SendCoreAsync(message.Method, message.Args);
                if (message.KickReason is not null)
                {
                    client.Context.Abort();
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogSendFailed(exception);
            client.Context.Abort();
        }
    }

    private void LogClosed(SpaceClient client, string reason, DateTimeOffset now) =>
        LogConnectionClosed(client.PlayerId, reason, (long)(now - client.ConnectedAt).TotalSeconds, client.Dropped.Total, client.Invalid.Total);

    [LoggerMessage(Level = LogLevel.Information, Message = "Space join: player {PlayerId}, instance {InstanceId}")]
    private partial void LogJoined(Guid playerId, int instanceId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Space connection closed: player {PlayerId}, reason {Reason}, {DurationSeconds} s, {Dropped} dropped, {Invalid} invalid")]
    private partial void LogConnectionClosed(Guid playerId, string reason, long durationSeconds, int dropped, int invalid);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Space send failed; the connection is closed")]
    private partial void LogSendFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Space shutdown: some connections did not close in 2 s")]
    private partial void LogShutdownTimeout();
}
