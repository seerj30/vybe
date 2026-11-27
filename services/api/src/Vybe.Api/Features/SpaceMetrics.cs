using System.Diagnostics.Metrics;

namespace Vybe.Api.Features;

/// <summary>Meter <c>Vybe.Spaces</c> (protocol doc §10): counts only, no player ids, no message content.</summary>
public sealed class SpaceMetrics
{
    public const string MeterName = "Vybe.Spaces";

    private readonly Counter<long> _messagesIn;
    private readonly Counter<long> _messagesOut;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _kicks;
    private readonly Histogram<double> _tick;

    public SpaceMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _messagesIn = meter.CreateCounter<long>("vybe.spaces.messages_in", description: "Client messages by type");
        _messagesOut = meter.CreateCounter<long>("vybe.spaces.messages_out", description: "Server messages by type");
        _rejected = meter.CreateCounter<long>("vybe.spaces.rejected", description: "Dropped or invalid client messages by reason");
        _kicks = meter.CreateCounter<long>("vybe.spaces.kicks", description: "Closed connections by kick reason");
        _tick = meter.CreateHistogram<double>("vybe.spaces.tick_duration", unit: "ms", description: "Duration of one server tick");
        meter.CreateObservableGauge("vybe.spaces.connections", () => Connections, description: "Open hub connections");
        meter.CreateObservableGauge("vybe.spaces.avatars", () => Avatars, description: "Avatars in instances");
        meter.CreateObservableGauge("vybe.spaces.instances", () => Instances, description: "Plaza instances");
        meter.CreateObservableGauge("vybe.spaces.rooms", () => Rooms, description: "Open friend rooms");
    }

    public int Connections { get; set; }

    public int Avatars { get; set; }

    public int Instances { get; set; }

    public int Rooms { get; set; }

    public void MessageIn(string type) => _messagesIn.Add(1, new KeyValuePair<string, object?>("type", type));

    public void MessagesOut(string type, int count) => _messagesOut.Add(count, new KeyValuePair<string, object?>("type", type));

    public void Rejected(string reason) => _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Kick(string reason) => _kicks.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Tick(double milliseconds) => _tick.Record(milliseconds);
}
