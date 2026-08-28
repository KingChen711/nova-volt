using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Forwarding;

/// <summary>The next durable hop for decoded device messages.</summary>
public interface IGatewayBatchSink
{
    /// <summary>Sends one or more messages to ingestion.</summary>
    Task SendAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken);
}
