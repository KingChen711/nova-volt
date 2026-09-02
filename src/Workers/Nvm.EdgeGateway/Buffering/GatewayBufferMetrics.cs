using System.Diagnostics.Metrics;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Các gauge quan sát được về buffer depth/bytes và các counter thiệt hại khi phục hồi.</summary>
public sealed class GatewayBufferMetrics : IDisposable
{
    /// <summary>Tên meter mà việc kết nối OpenTelemetry sau này sẽ export nguyên trạng.</summary>
    public const string MeterName = "Nvm.EdgeGateway";

    private readonly FileStoreAndForwardBuffer _buffer;
    private readonly Meter _meter = new(MeterName);

    /// <summary>Đăng ký bốn gauge cần thiết để vận hành một durable queue.</summary>
    public GatewayBufferMetrics(FileStoreAndForwardBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;

        _meter.CreateObservableGauge("gateway.buffer.depth", () => _buffer.Snapshot.Depth, unit: "messages");
        _meter.CreateObservableGauge("gateway.buffer.bytes", () => _buffer.Snapshot.Bytes, unit: "bytes");
        _meter.CreateObservableGauge(
            "gateway.buffer.corrupt_records",
            () => _buffer.Snapshot.CorruptRecords,
            unit: "records");
        _meter.CreateObservableGauge(
            "gateway.buffer.truncated_tails",
            () => _buffer.Snapshot.TruncatedTails,
            unit: "tails");
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
