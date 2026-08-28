using Nvm.Contracts.Events.Quality;

namespace Nvm.Ingestion.Publishing;

/// <summary>Carries committed measurements onward as domain events.</summary>
/// <remarks>
/// Called <b>after</b> the database transaction commits, never inside it. Ingestion and RabbitMQ are
/// two systems with no shared transaction, and pretending otherwise is what ADR-022 measured: 18 of
/// 200 events lost when the broker died mid-publish. M2 keeps that limit and counts it rather than
/// hiding it; the transactional outbox that closes it is M6.
/// </remarks>
public interface IMeasurementEventPublisher
{
    /// <summary>Publishes the events for one committed batch.</summary>
    /// <param name="events">Events for the readings that were actually stored.</param>
    /// <param name="cancellationToken">Stops the publish when the host shuts down.</param>
    /// <returns>How many failed to publish. The telemetry rows stay either way.</returns>
    Task<int> PublishAsync(
        IReadOnlyCollection<MeasurementRecorded> events,
        CancellationToken cancellationToken);
}
