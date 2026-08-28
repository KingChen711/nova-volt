using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug.Topics;

namespace Nvm.Sparkplug;

/// <summary>A Sparkplug message after its topic and payload have been joined at the gateway.</summary>
/// <remarks>
/// The payload alone has readings but no plant address; the topic has an address but no values.
/// Keeping both plus an explicit <see cref="SiteId"/> makes the server-side site boundary visible
/// on every object handed toward ingestion (K3).
/// </remarks>
public sealed record DecodedSparkplugMessage
{
    /// <summary>Creates a message that is safe to hand across the DMZ.</summary>
    public DecodedSparkplugMessage(
        string siteId,
        EquipmentPath equipmentPath,
        SparkplugTopic topic,
        DateTimeOffset gatewayTimestamp,
        ImmutableArray<DeviceReading> readings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentNullException.ThrowIfNull(equipmentPath);
        ArgumentNullException.ThrowIfNull(topic);

        if (readings.IsDefault)
        {
            throw new ArgumentException("Readings must be an initialized immutable array.", nameof(readings));
        }

        if (!string.Equals(siteId, equipmentPath.SiteId, StringComparison.Ordinal)
            || !string.Equals(siteId, topic.SiteId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Site '{siteId}', equipment '{equipmentPath}' and topic '{topic}' do not name one plant.",
                nameof(siteId));
        }

        SiteId = siteId;
        EquipmentPath = equipmentPath;
        Topic = topic;
        GatewayTimestamp = gatewayTimestamp;
        Readings = readings;
    }

    /// <summary>The plant this data belongs to (K3).</summary>
    public string SiteId { get; }

    /// <summary>The full ISA-95 address resolved against the active factory model.</summary>
    public EquipmentPath EquipmentPath { get; }

    /// <summary>The MQTT address exactly as received.</summary>
    public SparkplugTopic Topic { get; }

    /// <summary>When the gateway received the MQTT publish.</summary>
    public DateTimeOffset GatewayTimestamp { get; }

    /// <summary>The decoded readings carried by the publish.</summary>
    public ImmutableArray<DeviceReading> Readings { get; }

    /// <summary>Compares values rather than the backing identity of two immutable arrays.</summary>
    public bool Equals(DecodedSparkplugMessage? other) =>
        other is not null
        && string.Equals(SiteId, other.SiteId, StringComparison.Ordinal)
        && EquipmentPath == other.EquipmentPath
        && Topic == other.Topic
        && GatewayTimestamp == other.GatewayTimestamp
        && Readings.AsSpan().SequenceEqual(other.Readings.AsSpan());

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(SiteId, EquipmentPath, Topic, GatewayTimestamp, Readings.Length);
}
