using MassTransit;
using Nvm.Contracts.Events.Quality;

namespace Nvm.Ingestion.Publishing;

/// <summary>Publishes through the M1 bus, envelope and all.</summary>
/// <remarks>
/// <para>
/// <c>IPublishEndpoint</c> and nothing else. The CloudEvents attributes — <c>ce_id</c> off the
/// payload's <c>EventId</c>, type, source, time — are stamped by the send filter
/// <c>UseNvmCloudEvents</c> installed in M1. A second publish path built here would be a second
/// place for the envelope to be wrong, and the one place nobody would think to check when a header
/// went missing.
/// </para>
/// <para>
/// A failed publish is counted and swallowed. The rows are already committed; throwing would fail an
/// HTTP request the gateway would then retry, and the retry would insert nothing (dedup) and publish
/// again — turning one lost event into an endless one.
/// </para>
/// </remarks>
public sealed partial class BusMeasurementEventPublisher : IMeasurementEventPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<BusMeasurementEventPublisher> _logger;

    /// <summary>Creates the publisher over the shared bus endpoint.</summary>
    /// <param name="publishEndpoint">MassTransit's publish pipe, already carrying the M1 filters.</param>
    /// <param name="logger">Structured log sink.</param>
    /// <remarks>
    /// No metrics dependency: the caller owns the count, because the caller is the one that knows the
    /// rows are already committed. Counting in both places is how a number ends up doubled.
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
                // Per event, not per batch. One unroutable message must not take the rest of a batch
                // with it: those events are publishable, and the broker being unhappy about one of
                // them says nothing about the others.
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
