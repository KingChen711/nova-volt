using Nvm.EdgeGateway.Forwarding;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Reads durable records, POSTs them, then advances the cursor.</summary>
/// <remarks>
/// C09 deliberately uses a fixed retry pause and no rate limit. C10 replaces that policy with
/// Retry-After, jitter and a measured flush rate; the ordering here already makes the durability
/// rule explicit: HTTP success first, cursor fsync second.
/// </remarks>
public sealed partial class StoreAndForwardFlusher : BackgroundService
{
    private readonly FileStoreAndForwardBuffer _buffer;
    private readonly IGatewayBatchSink _sink;
    private readonly PersistentBufferOptions _options;
    private readonly GatewayCounters _counters;
    private readonly TimeProvider _clock;
    private readonly ILogger<StoreAndForwardFlusher> _logger;
    private readonly int _logEvery;
    private long _failures;

    /// <summary>Creates the single consumer of the buffer cursor.</summary>
    public StoreAndForwardFlusher(
        FileStoreAndForwardBuffer buffer,
        GatewayBufferMetrics metrics,
        IGatewayBatchSink sink,
        PersistentBufferOptions options,
        EdgeGatewayOptions gatewayOptions,
        GatewayCounters counters,
        TimeProvider clock,
        ILogger<StoreAndForwardFlusher> logger)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _buffer = buffer;
        _sink = sink;
        _options = options;
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

            try
            {
                await _sink.SendAsync(pendingMessages!, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failure = ++_failures;

                if (failure == 1 || failure % _logEvery == 0)
                {
                    FlushFailed(
                        _logger,
                        exception,
                        failure,
                        _buffer.Snapshot.Depth,
                        _options.FlushRetryDelay);
                }

                await Task.Delay(_options.FlushRetryDelay, _clock, stoppingToken);
                continue;
            }

            // A cursor fsync failure is a local storage fault, not HTTP backpressure. Let it stop
            // the host so restart recovery can decide whether HTTP must be repeated; retrying this
            // in-process after a partially completed cursor update can violate the outstanding-read
            // invariant.
            await _buffer.AcknowledgeAsync(pending, stoppingToken);
            _counters.CountForwarded(pendingMessages!.Length);
            _failures = 0;
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
}
