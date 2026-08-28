using System.Collections.Immutable;
using Nvm.EdgeGateway.Sessions;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway.Decoding;

/// <summary>Joins an MQTT topic to decoded readings and stamps the gateway clock.</summary>
/// <remarks>
/// Session state — alias tables, <c>bdSeq</c>, <c>seq</c> and liveness — belongs to
/// <see cref="NodeSessionTracker"/>, not here. This type reads bytes and hands the tracker what it
/// learned; the tracker decides what that means for the node. Keeping the two apart is what makes
/// "an NDEATH must not write telemetry" a structural fact rather than a rule someone has to
/// remember: a death never produces readings for this method to return.
/// </remarks>
public sealed class SparkplugMessageDecoder
{
    private readonly NodeSessionTracker _sessions;
    private readonly IEquipmentDirectory _equipment;
    private readonly TimeProvider _clock;

    /// <summary>Creates a decoder over the model currently active at each site.</summary>
    /// <param name="sessions">Owner of alias tables and node liveness.</param>
    /// <param name="equipment">The model each plant is running.</param>
    /// <param name="clock">Stamps the moment the gateway received the publish (K1).</param>
    public SparkplugMessageDecoder(
        NodeSessionTracker sessions,
        IEquipmentDirectory equipment,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(clock);

        _sessions = sessions;
        _equipment = equipment;
        _clock = clock;
    }

    /// <summary>Decodes a birth/data publish, or returns null for a message that carries no readings.</summary>
    /// <param name="topicValue">The MQTT topic exactly as received.</param>
    /// <param name="payload">The Sparkplug B bytes.</param>
    /// <returns>A message to forward, or null for a death, a command or a host-state publish.</returns>
    /// <exception cref="UnknownMetricAliasException">
    /// A metric named itself only by an alias this session never declared. A rebirth is requested
    /// before the exception leaves, because the exception is what stops the message and the request
    /// is what makes the next one readable.
    /// </exception>
    public DecodedSparkplugMessage? Decode(string? topicValue, ReadOnlySpan<byte> payload)
    {
        // Stamp before parsing or awaiting anything. This is when the gateway received the publish,
        // not when a downstream HTTP request happened to finish.
        var gatewayTimestamp = _clock.GetUtcNow();

        if (SparkplugTopic.IsHostState(topicValue))
        {
            return null;
        }

        var topic = SparkplugTopic.Parse(topicValue);
        var equipmentPath = topic.ResolveEquipmentPath(_equipment)
            ?? throw new UnknownEquipmentTopicException(topic.Value);

        var readings = topic.MessageType switch
        {
            SparkplugMessageType.NodeBirth or SparkplugMessageType.DeviceBirth => DecodeBirth(topic, payload),
            SparkplugMessageType.NodeData or SparkplugMessageType.DeviceData => DecodeData(topic, payload),
            SparkplugMessageType.NodeDeath or SparkplugMessageType.DeviceDeath => DecodeDeath(topic, payload),
            _ => default,
        };

        return readings.IsDefaultOrEmpty
            ? null
            : new DecodedSparkplugMessage(
                topic.SiteId,
                equipmentPath,
                topic,
                gatewayTimestamp,
                readings);
    }

    private ImmutableArray<DeviceReading> DecodeBirth(SparkplugTopic topic, ReadOnlySpan<byte> payload)
    {
        var birth = SparkplugPayload.DecodeBirth(payload);
        _sessions.ObserveBirth(topic, birth);
        return ForwardableReadings(birth.Readings);
    }

    // Sparkplug's own bookkeeping stops here. An NBIRTH carries bdSeq and the control metrics and
    // usually nothing else, so this normally leaves nothing to forward and the birth is consumed by
    // the tracker alone — which is the right outcome: the session opening is not a measurement.
    private static ImmutableArray<DeviceReading> ForwardableReadings(ImmutableArray<DeviceReading> readings)
    {
        if (readings.IsDefaultOrEmpty || !readings.Any(reading => SparkplugPayload.IsProtocolMetric(reading.MetricName)))
        {
            return readings;
        }

        return [.. readings.Where(reading => !SparkplugPayload.IsProtocolMetric(reading.MetricName))];
    }

    private ImmutableArray<DeviceReading> DecodeData(SparkplugTopic topic, ReadOnlySpan<byte> payload)
    {
        var aliases = _sessions.AliasesFor(topic);

        ImmutableArray<DeviceReading> readings;
        ulong? sequence;

        try
        {
            readings = SparkplugPayload.DecodeData(payload, aliases, out sequence);
        }
        catch (UnknownMetricAliasException)
        {
            // An alias this session never declared means our picture of the session is behind the
            // node's, which is the same condition a sequence gap reports and has the same only cure.
            // Guessing the metric would file real readings under the wrong name — the failure mode
            // that produces no error at all and corrupts every yield number computed from it.
            _sessions.RequestRebirth(topic);
            throw;
        }

        _sessions.ObserveData(topic, readings, sequence);
        return ForwardableReadings(readings);
    }

    private ImmutableArray<DeviceReading> DecodeDeath(SparkplugTopic topic, ReadOnlySpan<byte> payload)
    {
        // Node level only. A DDEATH says one device stopped reporting and does not carry a bdSeq, so
        // it cannot end a session; treating it as one would let a single failing channel mark a
        // thousand healthy ones stale.
        if (topic.MessageType == SparkplugMessageType.NodeDeath)
        {
            _sessions.ObserveDeath(topic, SparkplugPayload.DecodeDeath(payload));
        }

        // Empty, and this is the whole of D4's structural half: a death produces no readings, so
        // there is nothing for the buffer to carry, nothing for ingestion to insert, and no path by
        // which "the node is gone" could ever become "the history is gone" (K4).
        return [];
    }
}
