using System.Collections.Immutable;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.Simulator.Formation;

/// <summary>Một message mà line đã soạn xong, và nó gánh bao nhiêu phần của vế trái D1.</summary>
/// <param name="Message">Sparkplug message, đúng y như lúc nó sẽ được publish.</param>
/// <param name="Measurements">
/// Message này thêm bao nhiêu measurement vào <see cref="FormationLine.MeasurementCount"/> tại thời
/// điểm nó được soạn. Bằng 0 cho birth của chính node, vì birth đó chỉ mang protocol metric, và bằng
/// 0 cho <c>DBIRTH</c> của một rebirth, vì nó chỉ nhắc lại các reading đã được đếm từ lần đầu chúng
/// được lấy.
/// </param>
/// <remarks>
/// <para>
/// Bản thân con số này được lấy tại nơi channel được đọc, không phải ở đây — xem
/// <see cref="FormationChannel.MeasurementCount"/> để biết vì sao vế đó của phép đối chiếu phải mô
/// tả nhà máy chứ không phải đường truyền. Thứ type này mang theo là con số một message
/// <b>chịu trách nhiệm</b>, để một batch mà worker không hoàn thành được có thể gọi tên chính xác:
/// <see cref="SimulatorWorker.AbandonedMeasurements"/> chính là tổng đó, và D1 với D3 đòi hỏi nó
/// phải bằng 0 chứ không phải bị trừ đi khỏi bất cứ thứ gì.
/// </para>
/// <para>
/// Cố tình tách thành một type riêng khỏi <see cref="SparkplugMessage"/>: message trên dây là một
/// contract chia sẻ với gateway, còn phần đếm đi kèm là của riêng simulator.
/// </para>
/// </remarks>
public sealed record ComposedMessage(SparkplugMessage Message, int Measurements)
{
    /// <summary>Message đang đi đâu. Đọc xuyên qua <see cref="Message"/>.</summary>
    /// <remarks>
    /// Một facade phủ lên hai phần của message mà caller từng cần đến, để việc mang theo phần đếm
    /// bên cạnh không bắt mọi nơi đọc phải unwrap nó. Việc publish vẫn nhận
    /// <see cref="Message"/> một cách tường minh — nơi duy nhất không được phép quên mình đang cầm
    /// cái nào trong hai thứ này.
    /// </remarks>
    public SparkplugTopic Topic => Message.Topic;

    /// <summary>Payload đã encode. Đọc xuyên qua <see cref="Message"/>.</summary>
    public ImmutableArray<byte> Payload => Message.Payload;
}
