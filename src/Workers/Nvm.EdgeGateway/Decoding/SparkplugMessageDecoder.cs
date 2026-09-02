using System.Collections.Immutable;
using Nvm.EdgeGateway.Sessions;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway.Decoding;

/// <summary>Gắn một MQTT topic với các reading đã decode và đóng dấu đồng hồ của gateway.</summary>
/// <remarks>
/// State của session — bảng alias, <c>bdSeq</c>, <c>seq</c> và liveness — thuộc về
/// <see cref="NodeSessionTracker"/>, không thuộc về đây. Type này đọc byte và giao lại cho tracker
/// những gì nó biết được; tracker quyết định điều đó có ý nghĩa gì với node. Tách riêng hai việc
/// này là điều khiến "một NDEATH không được ghi telemetry" trở thành một sự thật mang tính cấu
/// trúc thay vì một quy tắc ai đó phải nhớ: một death không bao giờ tạo ra reading để method này
/// trả về.
/// </remarks>
public sealed class SparkplugMessageDecoder
{
    private readonly NodeSessionTracker _sessions;
    private readonly IEquipmentDirectory _equipment;
    private readonly TimeProvider _clock;

    /// <summary>Tạo một decoder dựa trên model đang active ở mỗi site.</summary>
    /// <param name="sessions">Chủ sở hữu của bảng alias và liveness của node.</param>
    /// <param name="equipment">Model mà mỗi nhà máy đang chạy.</param>
    /// <param name="clock">Đóng dấu thời điểm gateway nhận được publish (K1).</param>
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

    /// <summary>Decode một birth/data publish, hoặc trả về null cho một message không mang reading nào.</summary>
    /// <param name="topicValue">MQTT topic đúng như đã nhận được.</param>
    /// <param name="payload">Các byte Sparkplug B.</param>
    /// <returns>Một message để forward, hoặc null cho một death, một command hay một host-state publish.</returns>
    /// <exception cref="UnknownMetricAliasException">
    /// Một metric chỉ tự định danh bằng một alias mà session này chưa từng khai báo. Một rebirth
    /// được yêu cầu trước khi exception thoát ra, vì exception là thứ chặn message lại còn yêu cầu
    /// rebirth là thứ khiến message kế tiếp đọc được.
    /// </exception>
    public DecodedSparkplugMessage? Decode(string? topicValue, ReadOnlySpan<byte> payload)
    {
        // Đóng dấu trước khi parse hay await bất cứ thứ gì. Đây là thời điểm gateway nhận được
        // publish, không phải thời điểm một HTTP request downstream nào đó tình cờ hoàn tất.
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

    // Việc sổ sách nội bộ của Sparkplug dừng ở đây. Một NBIRTH mang bdSeq và các metric control, và
    // thường không mang gì khác, nên bình thường sẽ không còn gì để forward và birth chỉ được
    // tracker tiêu thụ một mình — đó chính là kết quả đúng: việc mở session không phải là một phép
    // đo.
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
            // Một alias mà session này chưa từng khai báo nghĩa là bức tranh session của ta đang
            // chậm hơn so với node, cùng một tình trạng mà một sequence gap báo cáo và chỉ có cùng
            // một cách chữa. Đoán mò metric sẽ file các reading thật dưới tên sai — kiểu lỗi không
            // hề tạo ra bất kỳ error nào cả mà lại làm hỏng mọi con số yield được tính từ nó.
            _sessions.RequestRebirth(topic);
            throw;
        }

        _sessions.ObserveData(topic, readings, sequence);
        return ForwardableReadings(readings);
    }

    private ImmutableArray<DeviceReading> DecodeDeath(SparkplugTopic topic, ReadOnlySpan<byte> payload)
    {
        // Chỉ ở cấp node. Một DDEATH nói rằng một device đã ngừng báo cáo và không mang bdSeq, nên
        // nó không thể kết thúc một session; xử lý nó như thể có thể sẽ để một channel lỗi duy nhất
        // đánh dấu hàng ngàn channel khỏe mạnh khác thành stale.
        if (topic.MessageType == SparkplugMessageType.NodeDeath)
        {
            _sessions.ObserveDeath(topic, SparkplugPayload.DecodeDeath(payload));
        }

        // Rỗng, và đây chính là toàn bộ nửa mang tính cấu trúc của D4: một death không tạo ra
        // reading nào, nên không có gì để buffer mang theo, không có gì để ingestion insert, và
        // không có con đường nào để "node đã biến mất" có thể trở thành "lịch sử đã biến mất" (K4).
        return [];
    }
}
