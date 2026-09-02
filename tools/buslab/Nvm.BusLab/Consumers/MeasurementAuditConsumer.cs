using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.Quality;

namespace Nvm.BusLab.Consumers;

/// <summary>Đóng vai service ghi mọi measurement đã được accept vào một audit trail.</summary>
/// <param name="logger">Nơi một dòng bằng chứng cho mỗi message đi tới.</param>
/// <remarks>
/// Một consumer thứ hai trên một queue thứ hai, và đó chính là toàn bộ mục đích của D1: nó phải thấy
/// mọi message mà cache consumer thấy, mà không consumer nào lấy mất message của consumer kia. Một
/// audit trail thiếu mất một nửa entry vì hai consumer dùng chung một queue là một defect mà không
/// gì báo cáo được.
/// </remarks>
[BusEndpoint("quality", "measurement-audit")]
public sealed partial class MeasurementAuditConsumer(ILogger<MeasurementAuditConsumer> logger)
    : IConsumer<MeasurementRecorded>
{
    private readonly ILogger<MeasurementAuditConsumer> _logger = logger;

    /// <inheritdoc />
    public Task Consume(ConsumeContext<MeasurementRecorded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;

        // device_timestamp, không phải OccurredAt. Một audit trail chỉ ghi lại thời điểm hệ thống
        // accept một reading sẽ không trả lời được câu hỏi mà một auditor đặt ra, đó là khi nào cell
        // được đo (scope.md §7.3).
        Recorded(message.SignalCode, message.EquipmentPath, message.DeviceTimestamp, message.EventId);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "measurement-audit recorded {SignalCode} at {EquipmentPath}, measured {DeviceTimestamp} "
            + "(ce_id {EventId})")]
    private partial void Recorded(
        string signalCode,
        string equipmentPath,
        DateTimeOffset deviceTimestamp,
        Guid eventId);
}
