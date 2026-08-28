using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.Quality;

namespace Nvm.BusLab.Consumers;

/// <summary>Stands in for the service that writes every accepted measurement to an audit trail.</summary>
/// <param name="logger">Where the one line of evidence per message goes.</param>
/// <remarks>
/// A second consumer on a second queue, and the whole point of D1: it must see every message the
/// cache consumer sees, without either one taking a message from the other. An audit trail that is
/// missing half its entries because two consumers shared a queue is a defect nothing reports.
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

        // device_timestamp, not OccurredAt. An audit trail that recorded only when the system
        // accepted a reading could not answer the question an auditor asks, which is when the cell
        // was measured (scope.md §7.3).
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
