using System.Diagnostics.Metrics;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Công bố buffer đang xả cạn nhanh cỡ nào và cái gì đang kìm nó lại.</summary>
/// <remarks>
/// Chỉ riêng depth trả lời được "backlog có đang giảm không"; nó không trả lời được "tại sao nó
/// đang giảm ở tốc độ này". Lab §5.C10.2 cần câu hỏi thứ hai được trả lời, vì một quá trình xả
/// cạn chậm do chính rate limit của ta và một quá trình xả cạn chậm do ingestion trả về 503 trông
/// giống hệt nhau trên đồ thị depth nhưng lại mang ý nghĩa đối lập nhau.
/// </remarks>
public sealed class GatewayFlushMetrics : IDisposable
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(5);

    private readonly GatewayCounters _counters;
    private readonly TimeProvider _clock;
    private readonly Meter _meter = new(GatewayBufferMetrics.MeterName);
    private readonly Lock _window = new();
    private long _windowStartTimestamp;
    private long _windowMessages;
    private double _messagesPerSecond;

    /// <summary>Đăng ký các instrument đo nhịp độ flush bên cạnh các gauge của buffer.</summary>
    /// <param name="counters">Process counter mà các gauge throttle đọc dữ liệu từ đó.</param>
    /// <param name="clock">Đồng hồ đo window tốc độ (K1).</param>
    public GatewayFlushMetrics(GatewayCounters counters, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(clock);

        _counters = counters;
        _clock = clock;
        _windowStartTimestamp = clock.GetTimestamp();

        _meter.CreateObservableGauge(
            "gateway.flush.rate",
            () => MessagesPerSecond,
            unit: "{message}/s");
        _meter.CreateObservableCounter(
            "gateway.flush.throttled",
            () => _counters.ThrottledFlushes,
            unit: "{response}");
        _meter.CreateObservableCounter(
            "gateway.flush.rate_limited",
            () => _counters.RateLimitedFlushes,
            unit: "{batch}");
    }

    /// <summary>Số message mỗi giây, đo trên window vừa hoàn tất gần nhất.</summary>
    public double MessagesPerSecond
    {
        get
        {
            lock (_window)
            {
                Roll(0);
                return _messagesPerSecond;
            }
        }
    }

    /// <summary>Ghi nhận số message ingestion đã chấp nhận và cursor đã đi qua.</summary>
    /// <param name="messages">Số message trong batch vừa được acknowledge.</param>
    public void RecordFlushed(int messages)
    {
        lock (_window)
        {
            Roll(messages);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private void Roll(long messages)
    {
        _windowMessages += messages;
        var elapsed = _clock.GetElapsedTime(_windowStartTimestamp);

        if (elapsed < WindowLength)
        {
            return;
        }

        _messagesPerSecond = _windowMessages / elapsed.TotalSeconds;
        _windowMessages = 0;
        _windowStartTimestamp = _clock.GetTimestamp();
    }
}
