using Nvm.EdgeGateway.Forwarding;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Reads durable records, POSTs them at a bounded rate, then advances the cursor.</summary>
/// <remarks>
/// Two independent brakes, and they exist for different reasons. The rate limiter is what this
/// gateway promises never to exceed even when it is the only one recovering. Backpressure is what
/// ingestion asks for when it is not coping — which usually means the other gateways in the area
/// came back at the same moment. Honouring only the first would still overwhelm a shared backend;
/// honouring only the second would need ingestion to be hurt before anyone slowed down.
/// </remarks>
public sealed partial class StoreAndForwardFlusher : BackgroundService
{
    private readonly FileStoreAndForwardBuffer _buffer;
    private readonly GatewayFlushMetrics _flushMetrics;
    private readonly IGatewayBatchSink _sink;
    private readonly PersistentBufferOptions _options;
    private readonly FlushRateLimiter _rateLimiter;
    private readonly FlushBackoff _backoff;
    private readonly GatewayCounters _counters;
    private readonly TimeProvider _clock;
    private readonly ILogger<StoreAndForwardFlusher> _logger;
    private readonly int _logEvery;
    private long _failures;

    /// <summary>Creates the single consumer of the buffer cursor.</summary>
    /// <param name="buffer">Durable queue this flusher drains.</param>
    /// <param name="metrics">Buffer depth gauges; constructed here so they are alive while draining.</param>
    /// <param name="flushMetrics">Rate and throttle instruments read by the D3 lab.</param>
    /// <param name="sink">Ingestion transport.</param>
    /// <param name="options">Flush batching, pacing and retry configuration.</param>
    /// <param name="rateLimiter">The gateway's own sustained-rate promise.</param>
    /// <param name="backoff">Retry schedule honouring any server-supplied floor.</param>
    /// <param name="gatewayOptions">Supplies the shared progress-log interval.</param>
    /// <param name="counters">Process counters.</param>
    /// <param name="clock">Clock behind every wait (K1).</param>
    /// <param name="logger">Structured log sink.</param>
    public StoreAndForwardFlusher(
        FileStoreAndForwardBuffer buffer,
        GatewayBufferMetrics metrics,
        GatewayFlushMetrics flushMetrics,
        IGatewayBatchSink sink,
        PersistentBufferOptions options,
        FlushRateLimiter rateLimiter,
        FlushBackoff backoff,
        EdgeGatewayOptions gatewayOptions,
        GatewayCounters counters,
        TimeProvider clock,
        ILogger<StoreAndForwardFlusher> logger)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(flushMetrics);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _buffer = buffer;
        _flushMetrics = flushMetrics;
        _sink = sink;
        _options = options;
        _rateLimiter = rateLimiter;
        _backoff = backoff;
        _logEvery = gatewayOptions.LogEvery;
        _counters = counters;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = _buffer.Snapshot;
        BufferRecovered(
            _logger,
            recovered.Depth,
            recovered.Bytes,
            recovered.CorruptRecords,
            recovered.TruncatedTails);

        BufferedBatch? pending = null;
        DecodedSparkplugMessage[]? pendingMessages = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (pending is null)
            {
                pending = await _buffer.ReadBatchAsync(
                    _options.FlushBatchSize,
                    _options.FlushBatchBytes,
                    stoppingToken);

                if (!pending.HasProgress)
                {
                    pending = null;
                    await Task.Delay(_options.FlushRetryDelay, _clock, stoppingToken);
                    continue;
                }

                if (pending.Payloads.IsEmpty)
                {
                    // Every physical record in this range failed CRC. Crossing it is explicit and
                    // counted; retrying the same corrupt bytes forever cannot restore their contents.
                    await _buffer.AcknowledgeAsync(pending, stoppingToken);
                    pending = null;
                    continue;
                }

                pendingMessages = pending.Payloads
                    .SelectMany(payload => SparkplugIngressBatchCodec.Decode(payload))
                    .ToArray();
            }

            // Pace before sending, not after failing. Waiting only once ingestion has complained
            // means the first burst of a recovery has already landed.
            var held = await _rateLimiter.AcquireAsync(pendingMessages!.Length, stoppingToken);

            if (held > TimeSpan.Zero)
            {
                var limited = _counters.CountRateLimited();

                if (limited == 1 || limited % _logEvery == 0)
                {
                    FlushRateLimited(_logger, limited, held, _buffer.Snapshot.Depth);
                }
            }

            try
            {
                await _sink.SendAsync(pendingMessages!, stoppingToken);
            }
            // Guarded on the stopping token, NOT on the exception type. HttpClient throws
            // TaskCanceledException when its own timeout elapses, and TaskCanceledException derives
            // from OperationCanceledException — so "is not OperationCanceledException" let every
            // ingestion timeout escape and kill the host. Measured in lab §5.C10.2: six gateway
            // restarts during one two-minute backend outage, each one re-reading the buffer, and a
            // drain that missed its three-minute budget by three seconds. Restarting the edge
            // because the backend is unreachable is exactly what N15 forbids.
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                var failure = ++_failures;
                var hint = (exception as GatewayBackpressureException)?.RetryAfter;
                var delay = _backoff.NextDelay(failure, hint);

                if (exception is GatewayBackpressureException backpressure)
                {
                    var throttled = _counters.CountThrottled();

                    if (throttled == 1 || throttled % _logEvery == 0)
                    {
                        FlushThrottled(
                            _logger,
                            throttled,
                            (int?)backpressure.StatusCode ?? 0,
                            delay,
                            _buffer.Snapshot.Depth);
                    }
                }
                else if (failure == 1 || failure % _logEvery == 0)
                {
                    FlushFailed(_logger, exception, failure, _buffer.Snapshot.Depth, delay);
                }

                await Task.Delay(delay, _clock, stoppingToken);
                continue;
            }

            // A cursor fsync failure is a local storage fault, not HTTP backpressure. Let it stop
            // the host so restart recovery can decide whether HTTP must be repeated; retrying this
            // in-process after a partially completed cursor update can violate the outstanding-read
            // invariant.
            await _buffer.AcknowledgeAsync(pending, stoppingToken);
            var forwarded = _counters.CountForwarded(pendingMessages!.Length);
            _counters.CountFlushBatch();
            _flushMetrics.RecordFlushed(pendingMessages!.Length);

            if (_failures > 0)
            {
                FlushRecovered(_logger, _failures, _buffer.Snapshot.Depth, _flushMetrics.MessagesPerSecond);
                _failures = 0;
            }

            if (forwarded % _logEvery < pendingMessages!.Length)
            {
                FlushProgress(
                    _logger,
                    forwarded,
                    _buffer.Snapshot.Depth,
                    _flushMetrics.MessagesPerSecond,
                    _counters.ThrottledFlushes,
                    _counters.RateLimitedFlushes);
            }

            pending = null;
            pendingMessages = null;
        }
    }

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Warning,
        Message = "Ingestion flush failed {FailureCount} times; buffer depth={BufferDepth}; retrying after {RetryDelay}")]
    private static partial void FlushFailed(
        ILogger logger,
        Exception exception,
        long failureCount,
        long bufferDepth,
        TimeSpan retryDelay);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Gateway buffer recovered: depth={Depth}, bytes={Bytes}, corrupt_records={CorruptRecords}, truncated_tails={TruncatedTails}")]
    private static partial void BufferRecovered(
        ILogger logger,
        long depth,
        long bytes,
        long corruptRecords,
        long truncatedTails);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Warning,
        Message = "Ingestion asked for backpressure {ThrottledCount} times (status={StatusCode}); slowing to {RetryDelay}; buffer depth={BufferDepth}")]
    private static partial void FlushThrottled(
        ILogger logger,
        long throttledCount,
        int statusCode,
        TimeSpan retryDelay,
        long bufferDepth);

    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Information,
        Message = "Flush rate limit held a batch {RateLimitedCount} times; latest wait {HeldFor}; buffer depth={BufferDepth}")]
    private static partial void FlushRateLimited(
        ILogger logger,
        long rateLimitedCount,
        TimeSpan heldFor,
        long bufferDepth);

    [LoggerMessage(
        EventId = 2205,
        Level = LogLevel.Information,
        Message = "Flush progress: forwarded={Forwarded}, buffer_depth={BufferDepth}, rate={FlushRate:F0}/s, throttled={Throttled}, rate_limited={RateLimited}")]
    private static partial void FlushProgress(
        ILogger logger,
        long forwarded,
        long bufferDepth,
        double flushRate,
        long throttled,
        long rateLimited);

    [LoggerMessage(
        EventId = 2206,
        Level = LogLevel.Information,
        Message = "Ingestion accepted a flush again after {FailureCount} failures; buffer depth={BufferDepth}, rate={FlushRate:F0}/s")]
    private static partial void FlushRecovered(
        ILogger logger,
        long failureCount,
        long bufferDepth,
        double flushRate);
}
