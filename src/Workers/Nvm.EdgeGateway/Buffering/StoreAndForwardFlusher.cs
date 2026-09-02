using Nvm.EdgeGateway.Forwarding;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Đọc các record durable, POST chúng ở một tốc độ có giới hạn, rồi đẩy cursor tiến lên.</summary>
/// <remarks>
/// Hai cái phanh độc lập, và chúng tồn tại vì những lý do khác nhau. Rate limiter là điều mà
/// gateway này cam kết không bao giờ vượt quá, kể cả khi nó là gateway duy nhất đang phục hồi.
/// Backpressure là điều ingestion yêu cầu khi nó không kham nổi — thường có nghĩa là các gateway
/// khác trong khu vực cùng quay lại vào đúng lúc đó. Chỉ tuân theo cái đầu vẫn có thể làm quá tải
/// một backend dùng chung; chỉ tuân theo cái sau thì cần ingestion phải bị tổn hại trước khi có ai
/// đó chậm lại.
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

    /// <summary>Tạo consumer duy nhất của cursor buffer.</summary>
    /// <param name="buffer">Hàng đợi durable mà flusher này xả cạn.</param>
    /// <param name="metrics">Các gauge buffer depth; được khởi tạo ở đây để chúng sống suốt quá trình xả cạn.</param>
    /// <param name="flushMetrics">Các instrument về rate và throttle mà lab D3 đọc.</param>
    /// <param name="sink">Kênh vận chuyển tới ingestion.</param>
    /// <param name="options">Cấu hình batching, pacing và retry của flush.</param>
    /// <param name="rateLimiter">Cam kết sustained-rate của chính gateway này.</param>
    /// <param name="backoff">Lịch retry tuân theo mức sàn mà server đưa ra (nếu có).</param>
    /// <param name="gatewayOptions">Cung cấp interval ghi log tiến độ dùng chung.</param>
    /// <param name="counters">Process counter.</param>
    /// <param name="clock">Đồng hồ đứng sau mọi lần chờ (K1).</param>
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
                    // Mọi record vật lý trong khoảng này đều fail CRC. Việc đi qua nó được thực hiện
                    // tường minh và có đếm; retry mãi mãi với cùng những byte hỏng không thể khôi
                    // phục nội dung của chúng.
                    await _buffer.AcknowledgeAsync(pending, stoppingToken);
                    pending = null;
                    continue;
                }

                pendingMessages = pending.Payloads
                    .SelectMany(payload => SparkplugIngressBatchCodec.Decode(payload))
                    .ToArray();
            }

            // Pace trước khi gửi, không phải sau khi thất bại. Chỉ chờ khi ingestion đã than phiền
            // nghĩa là burst đầu tiên của một lần phục hồi đã kịp đến rồi.
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
            // Được gác bởi stopping token, KHÔNG phải bởi loại exception. HttpClient ném
            // TaskCanceledException khi timeout của chính nó trôi qua, và TaskCanceledException kế
            // thừa từ OperationCanceledException — nên "is not OperationCanceledException" sẽ để
            // mọi timeout của ingestion thoát ra và giết host. Đã đo được trong lab §5.C10.2: sáu
            // lần gateway restart trong một lần backend outage kéo dài hai phút, mỗi lần đều đọc
            // lại buffer từ đầu, và một lần xả cạn trễ mất ba giây so với ngân sách ba phút của nó.
            // Restart edge chỉ vì backend không thể liên lạc được chính là điều N15 cấm.
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

            // Một lỗi fsync cursor là lỗi storage cục bộ, không phải HTTP backpressure. Cứ để nó
            // dừng host để quá trình phục hồi khi restart có thể quyết định liệu HTTP có cần lặp
            // lại hay không; retry việc này ngay trong tiến trình sau một lần cập nhật cursor dở
            // dang có thể vi phạm bất biến (invariant) về outstanding-read.
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
