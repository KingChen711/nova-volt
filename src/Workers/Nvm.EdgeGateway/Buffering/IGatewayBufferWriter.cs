using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Nhận các message MQTT đã decode một cách durable, trước khi acknowledgement QoS của chúng được gửi.</summary>
public interface IGatewayBufferWriter
{
    /// <summary>Xếp hàng các message đã decode và trả về một task hoàn tất tại thời điểm fsync của chúng.</summary>
    /// <remarks>
    /// Chờ giá trị bên ngoài giữ cho việc handoff trong bộ nhớ ở mức có giới hạn. MQTT callback
    /// không được chờ task bên trong: việc return cho phép broker deliver đủ số publish QoS 1 để
    /// chia sẻ chung một fsync; mỗi task bên trong chỉ gửi acknowledgement riêng của nó sau khi
    /// fsync chung đó hoàn tất.
    /// </remarks>
    ValueTask<Task> QueueAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken);
}
