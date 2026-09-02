using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Forwarding;

/// <summary>Chặng durable kế tiếp cho các message device đã decode.</summary>
public interface IGatewayBatchSink
{
    /// <summary>Gửi một hoặc nhiều message tới ingestion.</summary>
    Task SendAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken);
}
