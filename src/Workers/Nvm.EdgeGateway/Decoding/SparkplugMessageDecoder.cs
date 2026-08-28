using System.Collections.Concurrent;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway.Decoding;

/// <summary>Joins an MQTT topic to decoded readings and stamps the gateway clock.</summary>
/// <remarks>
/// C08 keeps one alias table per publisher and replaces it on birth. C11 adds the session rules:
/// reset all device tables on a new node birth, pair birth/death by bdSeq, and request rebirth on a
/// sequence gap. Keeping this state here makes that later change explicit rather than hidden in the
/// protobuf decoder.
/// </remarks>
public sealed class SparkplugMessageDecoder
{
    private readonly ConcurrentDictionary<string, MetricAliasTable> _aliases = new(StringComparer.Ordinal);
    private readonly IEquipmentDirectory _equipment;
    private readonly TimeProvider _clock;

    /// <summary>Creates a decoder over the model currently active at each site.</summary>
    public SparkplugMessageDecoder(IEquipmentDirectory equipment, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(clock);

        _equipment = equipment;
        _clock = clock;
    }

    /// <summary>Decodes a birth/data publish, or returns null for a valid message C11 owns.</summary>
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
            _ => default,
        };

        return readings.IsDefault
            ? null
            : new DecodedSparkplugMessage(
                topic.SiteId,
                equipmentPath,
                topic,
                gatewayTimestamp,
                readings);
    }

    private System.Collections.Immutable.ImmutableArray<DeviceReading> DecodeBirth(
        SparkplugTopic topic,
        ReadOnlySpan<byte> payload)
    {
        var birth = SparkplugPayload.DecodeBirth(payload);
        _aliases[PublisherKey(topic)] = birth.Aliases;
        return birth.Readings;
    }

    private System.Collections.Immutable.ImmutableArray<DeviceReading> DecodeData(
        SparkplugTopic topic,
        ReadOnlySpan<byte> payload)
    {
        var aliases = _aliases.GetValueOrDefault(PublisherKey(topic), MetricAliasTable.Empty);
        return SparkplugPayload.DecodeData(payload, aliases);
    }

    private static string PublisherKey(SparkplugTopic topic) =>
        string.Join('\u001f', topic.GroupId, topic.EdgeNodeId, topic.DeviceCode ?? string.Empty);
}
