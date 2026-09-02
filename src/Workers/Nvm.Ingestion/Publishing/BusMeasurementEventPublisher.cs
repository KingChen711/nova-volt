using MassTransit;
using Nvm.Contracts.Events.Quality;

namespace Nvm.Ingestion.Publishing;

/// <summary>Publish qua M1 bus, kèm cả envelope.</summary>
/// <remarks>
/// <para>
/// Chỉ <c>IPublishEndpoint</c> và không gì khác. Các thuộc tính CloudEvents — <c>ce_id</c> lấy từ
/// <c>EventId</c> của payload, type, source, time — được đóng dấu bởi send filter
/// <c>UseNvmCloudEvents</c> cài đặt trong M1. Một publish path thứ hai xây ở đây sẽ là một nơi thứ
/// hai để envelope có thể sai, và là nơi không ai nghĩ tới kiểm tra khi một header bị thiếu.
/// </para>
/// <para>
/// Một publish thất bại được đếm và nuốt đi. Các dòng đã commit rồi; throw ra sẽ làm fail một HTTP
/// request mà gateway sau đó sẽ retry, và retry đó sẽ không insert gì cả (dedup) rồi publish lại lần
/// nữa — biến một event bị mất thành một event bị mất mãi mãi.
/// </para>
/// </remarks>
public sealed partial class BusMeasurementEventPublisher : IMeasurementEventPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<BusMeasurementEventPublisher> _logger;

    /// <summary>Tạo publisher trên shared bus endpoint.</summary>
    /// <param name="publishEndpoint">Publish pipe của MassTransit, đã mang sẵn các filter của M1.</param>
    /// <param name="logger">Structured log sink.</param>
    /// <remarks>
    /// Không phụ thuộc metrics: caller sở hữu việc đếm, vì caller là bên biết các dòng đã commit
    /// rồi. Đếm ở cả hai nơi là cách một con số bị nhân đôi.
    /// </remarks>
    public BusMeasurementEventPublisher(
        IPublishEndpoint publishEndpoint,
        ILogger<BusMeasurementEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(publishEndpoint);
        ArgumentNullException.ThrowIfNull(logger);

        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PublishAsync(
        IReadOnlyCollection<MeasurementRecorded> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        var failed = 0;

        foreach (var measurement in events)
        {
            try
            {
                await _publishEndpoint.Publish(measurement, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Theo từng event, không phải theo batch. Một message không route được không được
                // kéo theo phần còn lại của batch: những event đó vẫn publish được, và việc broker
                // không hài lòng với một trong số chúng không nói lên điều gì về những cái còn lại.
                failed++;
                PublishFailed(_logger, exception, measurement.EventId, measurement.SignalCode);
            }
        }

        return failed;
    }

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Error,
        Message = "Could not publish MeasurementRecorded {EventId} for {SignalCode}; the telemetry row is stored and the event is lost (ADR-022)")]
    private static partial void PublishFailed(
        ILogger logger,
        Exception exception,
        Guid eventId,
        string signalCode);
}
