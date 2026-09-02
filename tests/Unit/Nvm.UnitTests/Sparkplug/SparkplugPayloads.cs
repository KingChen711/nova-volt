using Google.Protobuf;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Dựng các payload Sparkplug trong process, cho các trường hợp mà một fixture đã ghi lại
/// không thể bao phủ được.</summary>
/// <remarks>
/// <para>
/// Đây là encoder mà các fixture trong <c>tests/Fixtures/sparkplug/</c> cố tình tránh dùng, nên đáng
/// để nói rõ vì sao nó được cho phép ở đây. Các fixture đó trả lời câu hỏi <i>"bản copy schema của
/// chúng ta có giống với bản mà thiết bị dùng không"</i>, và một payload tự encode không thể trả lời
/// câu đó. Những cái này trả lời <i>"với một payload có hình dạng thế này, decoder tạo ra gì"</i> —
/// một câu hỏi về mapping của chúng ta, trên một schema mà các fixture và <c>SparkplugPinTests</c>
/// đã pin sẵn rồi.
/// </para>
/// <para>
/// Các type <c>Org.Eclipse.Tahu.Protobuf</c> được sinh ra chỉ xuất hiện ở đây và không nơi nào khác
/// trong test project. Đó là cùng ranh giới mà ADR-026 vạch ra cho production code, được giữ hiển thị
/// bằng cách gói việc băng qua ranh giới đó trong một file duy nhất.
/// </para>
/// </remarks>
internal static class SparkplugPayloads
{
    /// <summary>2026-08-28T07:15:30.500Z — cùng thời điểm mà các fixture đã ghi lại sử dụng.</summary>
    internal const ulong DefaultTimestampMs = 1787901330500;

    /// <summary>Encode một payload đúng như đã cho, kể cả những phần bị thiếu.</summary>
    internal static byte[] Encode(ulong? timestamp, ulong? seq, params SparkplugMetric[] metrics)
    {
        var payload = new Payload();

        if (timestamp is { } stamped)
        {
            payload.Timestamp = stamped;
        }

        if (seq is { } sequence)
        {
            payload.Seq = sequence;
        }

        payload.Metrics.AddRange(metrics);

        return payload.ToByteArray();
    }

    /// <summary>Một payload mang một metric, với payload timestamp đã được đặt.</summary>
    internal static byte[] Carrying(SparkplugMetric metric) =>
        Encode(DefaultTimestampMs, seq: 1, metric);

    /// <summary>Một birth khai báo các tên và alias đã cho, tất cả đều là Float.</summary>
    /// <remarks>
    /// Đủ dùng cho các test quan tâm tới <i>alias nào</i> một bảng đang giữ chứ không phải về
    /// datatype; <c>SparkplugValueTests</c> dựng metric từng cái một khi type mới là trọng tâm.
    /// </remarks>
    internal static byte[] BirthDeclaring(params (string Name, ulong Alias)[] metrics) =>
        BirthDeclaring(seq: 0, metrics);

    /// <summary>Cùng một birth ở một sequence number được chọn, cho các test về tính liên tục của
    /// <c>seq</c>.</summary>
    internal static byte[] BirthDeclaring(ulong seq, params (string Name, ulong Alias)[] metrics)
    {
        var built = Array.ConvertAll(metrics, metric => new SparkplugMetric
        {
            Name = metric.Name,
            Alias = metric.Alias,
            Datatype = (uint)DataType.Float,
            Timestamp = DefaultTimestampMs,
            FloatValue = 0f,
        });

        return Encode(DefaultTimestampMs, seq, built);
    }

    /// <summary>Một <c>NBIRTH</c> khai báo một session number và không gì khác đáng chú ý.</summary>
    internal static byte[] NodeBirth(ulong birthDeathSequence) =>
        Encode(DefaultTimestampMs, seq: 0, BirthDeathSequenceMetric(birthDeathSequence));

    /// <summary>Một <c>NDEATH</c> đúng như cách một broker publish nó từ một will đã đăng ký.</summary>
    internal static byte[] NodeDeath(ulong? birthDeathSequence) =>
        birthDeathSequence is { } session
            ? Encode(DefaultTimestampMs, seq: null, BirthDeathSequenceMetric(session))
            : Encode(DefaultTimestampMs, seq: null);

    /// <summary>Một data payload cho một alias đã khai báo, ở một sequence number được chọn.</summary>
    internal static byte[] DataAt(ulong seq, ulong alias, float value) =>
        Encode(
            DefaultTimestampMs,
            seq,
            new SparkplugMetric
            {
                Alias = alias,
                Timestamp = DefaultTimestampMs,
                FloatValue = value,
            });

    private static SparkplugMetric BirthDeathSequenceMetric(ulong birthDeathSequence) =>
        new()
        {
            // Không có alias, theo đúng specification: một will được soạn tại thời điểm connect,
            // trước birth — thứ lẽ ra đã gán cho nó một alias.
            Name = "bdSeq",
            Datatype = (uint)DataType.Int64,
            Timestamp = DefaultTimestampMs,
            LongValue = birthDeathSequence,
        };
}
