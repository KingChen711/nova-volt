using System.Collections.Immutable;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Simulator;

/// <summary>Đếm các measurement từ những payload đã publish, theo đúng cách pipeline đếm dòng.</summary>
/// <remarks>
/// <para>
/// Được decode chứ không phải đếm dồn. Một <c>DDATA</c> chỉ mang theo alias và không đọc được nếu
/// thiếu <c>DBIRTH</c> đã khai báo chúng, nên các alias table được xây dựng dần theo đúng thứ tự
/// publish — đếm trực tiếp các metric entry thô thay vào đó sẽ đếm phải thứ mà gateway không bao giờ
/// forward được và không database nào lưu được.
/// </para>
/// <para>
/// Các protocol metric bị bỏ ra dựa trên đúng lý do gateway cũng bỏ chúng ra: <c>bdSeq</c> và các
/// control metric là Sparkplug đang nói về session của chính nó, không thiết bị nào đo chúng cả, và
/// đếm chúng ở đây sẽ khiến vế phải của một phép đối soát vượt vế trái một lượng cố định cho mỗi
/// node birth.
/// </para>
/// <para>
/// Mọi message được đưa vào đều bị đếm, kể cả bản trùng lặp. Đây là cố ý — lớp này đại diện cho
/// những gì một consumer đã nhận được, nên caller nào muốn con số thật của nhà máy phải tự đưa vào
/// một stream không chứa bản trùng lặp nào bị chèn thêm.
/// </para>
/// </remarks>
internal sealed class MeasurementLedger
{
    private readonly Dictionary<string, MetricAliasTable> _aliases = new(StringComparer.Ordinal);

    /// <summary>Tổng số measurement mà mọi thứ đã thêm vào từ trước tới giờ mang theo.</summary>
    public long Total { get; private set; }

    /// <summary>Đọc một message và cộng thêm những gì một consumer có thể đã lưu được từ nó.</summary>
    public void Add(SparkplugMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var key = message.Topic.DeviceCode ?? string.Empty;
        ImmutableArray<DeviceReading> readings;

        if (message.Topic.MessageType is SparkplugMessageType.NodeBirth or SparkplugMessageType.DeviceBirth)
        {
            var birth = SparkplugPayload.DecodeBirth(message.Payload.AsSpan());

            _aliases[key] = birth.Aliases;
            readings = birth.Readings;
        }
        else
        {
            readings = SparkplugPayload.DecodeData(
                message.Payload.AsSpan(),
                _aliases.GetValueOrDefault(key, MetricAliasTable.Empty));
        }

        Total += readings.Count(reading => !SparkplugPayload.IsProtocolMetric(reading.MetricName));
    }

    /// <summary>Đọc một chuỗi message, theo đúng thứ tự chúng đã đi ra.</summary>
    public void AddAll(IEnumerable<SparkplugMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var message in messages)
        {
            Add(message);
        }
    }
}
