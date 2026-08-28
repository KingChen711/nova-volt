using Nvm.Contracts.Events.Quality;

namespace Nvm.Ingestion.Publishing;

/// <summary>Stores telemetry and announces nothing.</summary>
/// <remarks>
/// What a migration job, a unit test, or a deployment with no signals whitelisted runs with. It
/// exists so that "no bus configured" is a configuration, not a null check repeated at every call
/// site — and so that a missing publisher can never be mistaken for a publish that failed.
/// </remarks>
public sealed class NullMeasurementEventPublisher : IMeasurementEventPublisher
{
    /// <summary>The shared instance.</summary>
    public static NullMeasurementEventPublisher Instance { get; } = new();

    /// <inheritdoc />
    public Task<int> PublishAsync(
        IReadOnlyCollection<MeasurementRecorded> events,
        CancellationToken cancellationToken) => Task.FromResult(0);
}
