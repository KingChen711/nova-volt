using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug.Topics;

namespace Nvm.Sparkplug;

/// <summary>Một message Sparkplug sau khi topic và payload của nó đã được ghép lại ở gateway.</summary>
/// <remarks>
/// Riêng payload thì có readings nhưng không có địa chỉ nhà máy; riêng topic thì có địa chỉ nhưng
/// không có giá trị. Giữ cả hai cộng thêm một <see cref="SiteId"/> tường minh giúp ranh giới site
/// phía server hiện rõ trên mọi object được đưa vào ingestion (K3).
/// </remarks>
public sealed record DecodedSparkplugMessage
{
    /// <summary>Tạo một message an toàn để đưa qua DMZ.</summary>
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

    /// <summary>Nhà máy mà dữ liệu này thuộc về (K3).</summary>
    public string SiteId { get; }

    /// <summary>Địa chỉ ISA-95 đầy đủ, được resolve dựa trên factory model đang hoạt động.</summary>
    public EquipmentPath EquipmentPath { get; }

    /// <summary>Địa chỉ MQTT đúng như lúc nhận được.</summary>
    public SparkplugTopic Topic { get; }

    /// <summary>Thời điểm gateway nhận được MQTT publish.</summary>
    public DateTimeOffset GatewayTimestamp { get; }

    /// <summary>Các reading đã decode mà publish này mang theo.</summary>
    public ImmutableArray<DeviceReading> Readings { get; }

    /// <summary>So sánh theo giá trị thay vì theo identity nội bộ của hai immutable array.</summary>
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
