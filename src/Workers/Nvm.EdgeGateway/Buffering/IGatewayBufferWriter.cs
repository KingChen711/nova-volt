using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Durably accepts decoded MQTT messages before their QoS acknowledgement is sent.</summary>
public interface IGatewayBufferWriter
{
    /// <summary>Queues decoded messages and returns a task that completes at their fsync boundary.</summary>
    /// <remarks>
    /// Waiting for the outer value keeps the in-memory handoff bounded. The MQTT callback must not
    /// wait for the inner task: returning lets the broker deliver enough QoS 1 publishes to share an
    /// fsync; each inner task sends its own acknowledgement only after that shared fsync completes.
    /// </remarks>
    ValueTask<Task> QueueAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken);
}
