using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.Quality;

namespace Nvm.BusLab.Consumers;

/// <summary>Stands in for the service that keeps the latest evaluated value per channel.</summary>
/// <param name="logger">Where the one line of evidence per message goes.</param>
/// <remarks>
/// <para>
/// The real one arrives with Quality at M5: a grading decision reads the capacity a formation cycle
/// finished at, and a service holding a stale copy answers confidently and wrongly. That is why this
/// is the consumer worth imitating — the fan-out being demonstrated is not a demonstration.
/// </para>
/// <para>
/// Its own queue, not a share of one. Two consumers on one queue compete and each message reaches
/// exactly one of them; two consumers on two queues both receive every message. The difference is
/// invisible in code and decides whether the audit trail alongside it is complete or missing half
/// its entries.
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

    // Source-generated rather than a plain _logger.LogInformation(...). CA1873 refuses an
    // Information-level call carrying more than one property: the arguments are boxed into an array
    // before anything asks whether the level is switched on.
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "measurement-cache received {SignalCode} at {EquipmentPath}, clock {ClockQuality} "
            + "(ce_id {EventId})")]
    private partial void Received(string signalCode, string equipmentPath, string clockQuality, Guid eventId);
}
