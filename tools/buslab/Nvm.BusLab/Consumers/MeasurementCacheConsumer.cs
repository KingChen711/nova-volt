using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.Quality;

namespace Nvm.BusLab.Consumers;

/// <summary>Đóng vai service giữ giá trị mới nhất đã evaluate cho mỗi channel.</summary>
/// <param name="logger">Nơi một dòng bằng chứng cho mỗi message đi tới.</param>
/// <remarks>
/// <para>
/// Service thật sẽ đến cùng Quality ở M5: một quyết định grading đọc capacity mà một formation cycle
/// kết thúc ở đó, và một service giữ bản copy cũ sẽ trả lời một cách tự tin và sai. Đó là lý do
/// consumer này đáng để mô phỏng — cái fan-out đang được minh họa ở đây không phải là một minh họa
/// suông.
/// </para>
/// <para>
/// Queue riêng của nó, không phải một phần chia sẻ của queue nào khác. Hai consumer trên một queue
/// cạnh tranh nhau và mỗi message chỉ tới đúng một trong hai; hai consumer trên hai queue thì cả hai
/// đều nhận mọi message. Sự khác biệt này vô hình trong code và quyết định việc audit trail đi kèm
/// có đầy đủ hay thiếu mất một nửa entry.
/// </para>
/// </remarks>
[BusEndpoint("quality", "measurement-cache")]
public sealed partial class MeasurementCacheConsumer(ILogger<MeasurementCacheConsumer> logger)
    : IConsumer<MeasurementRecorded>
{
    private readonly ILogger<MeasurementCacheConsumer> _logger = logger;

    /// <inheritdoc />
    public Task Consume(ConsumeContext<MeasurementRecorded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;

        Received(message.SignalCode, message.EquipmentPath, message.ClockQuality, message.EventId);

        return Task.CompletedTask;
    }

    // Source-generated thay vì một lời gọi _logger.LogInformation(...) trần trụi. CA1873 từ chối một
    // lời gọi cấp Information mang hơn một property: các argument bị box vào một array trước khi bất
    // cứ thứ gì kiểm tra xem level có được bật hay không.
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "measurement-cache received {SignalCode} at {EquipmentPath}, clock {ClockQuality} "
            + "(ce_id {EventId})")]
    private partial void Received(string signalCode, string equipmentPath, string clockQuality, Guid eventId);
}
