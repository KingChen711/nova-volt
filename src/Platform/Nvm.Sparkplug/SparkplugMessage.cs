using System.Collections.Immutable;
using Nvm.Sparkplug.Topics;

namespace Nvm.Sparkplug;

/// <summary>Một lần publish MQTT: gửi tới đâu và mang theo gì.</summary>
/// <param name="Topic">Topic — nơi duy nhất chứa địa chỉ.</param>
/// <param name="Payload">Payload Sparkplug B đã encode.</param>
/// <remarks>
/// Hai thứ này đi cùng nhau vì tách riêng thì không cái nào có nghĩa — một payload không có topic
/// thì không nêu tên máy nào cả, và đây chính là cặp mà một publisher đưa cho broker rồi một
/// subscriber nhận lại.
/// </remarks>
public sealed record SparkplugMessage(SparkplugTopic Topic, ImmutableArray<byte> Payload)
{
    /// <summary>So sánh topic và các byte.</summary>
    /// <remarks>
    /// <see cref="ImmutableArray{T}"/> so sánh theo identity của mảng nó bọc, nên equality tự sinh của
    /// record sẽ coi hai lần publish giống hệt nhau là khác nhau. Điều đó sẽ vô hình cho tới khi một
    /// test so sánh message mong đợi với message thực tế bắt đầu fail mà không ai thấy lý do gì trong
    /// các giá trị.
    /// </remarks>
    public bool Equals(SparkplugMessage? other) =>
        other is not null
        && Topic == other.Topic
        && Payload.AsSpan().SequenceEqual(other.Payload.AsSpan());

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Topic, Payload.Length);
}
