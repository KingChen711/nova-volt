using System.Diagnostics.Metrics;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Publishes how fast the buffer is draining and what is holding it back.</summary>
/// <remarks>
/// Depth alone answers "is the backlog going down"; it cannot answer "why is it going down at
/// this speed". Lab §5.C10.2 needs the second question answered, because a slow drain caused by
/// our own rate limit and a slow drain caused by ingestion returning 503 look identical on a
/// depth graph and mean opposite things.
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

    /// <summary>Registers the flush-pacing instruments alongside the buffer gauges.</summary>
    /// <param name="counters">Process counters the throttle gauges read from.</param>
    /// <param name="clock">Clock measuring the rate window (K1).</param>
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

    /// <summary>Messages per second measured over the most recently completed window.</summary>
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

    /// <summary>Records messages ingestion has accepted and the cursor has crossed.</summary>
    /// <param name="messages">Messages in the batch just acknowledged.</param>
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
