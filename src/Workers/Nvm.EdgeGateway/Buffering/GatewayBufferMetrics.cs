using System.Diagnostics.Metrics;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Observable buffer depth/bytes and recovery damage counters.</summary>
public sealed class GatewayBufferMetrics : IDisposable
{
    /// <summary>The meter name future OpenTelemetry wiring exports unchanged.</summary>
    public const string MeterName = "Nvm.EdgeGateway";

    private readonly FileStoreAndForwardBuffer _buffer;
    private readonly Meter _meter = new(MeterName);

    /// <summary>Registers the four gauges required to operate a durable queue.</summary>
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
