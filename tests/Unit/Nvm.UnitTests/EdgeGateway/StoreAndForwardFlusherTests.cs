using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Buffering;
using Nvm.EdgeGateway.Forwarding;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class StoreAndForwardFlusherTests
{
    [Fact]
    public async Task SinkFailure_RetriesOutstandingBatchBeforeReadingTheNextRecord()
    {
        var options = Options();
        var sink = new FailFirstSink();

        await using var harness = await FlusherHarness.StartAsync(options, sink, [Message("first"), Message("second")]);

        await sink.ThirdCall.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.WaitForDrainAsync();

        sink.MetricNames.ShouldBe(["first", "first", "second"]);
        harness.Counters.ForwardedMessages.ShouldBe(2);
        harness.Counters.FlushBatches.ShouldBe(2);
        harness.Buffer.Snapshot.Depth.ShouldBe(0);
        harness.Flusher.ExecuteTask?.IsFaulted.ShouldBeFalse();
    }

    [Fact]
    public async Task Backpressure_WaitsAtLeastTheDelayIngestionAskedFor()
    {
        // Tự thân, độ trễ đầu tiên sẽ là 5 ms. Ingestion yêu cầu 300 ms, nên một gateway phớt lờ
        // header này sẽ quay lại từ rất lâu trước khi server nói rằng nó đã chịu đựng nổi.
        var options = Options();
        options.FlushRetryDelay = TimeSpan.FromMilliseconds(5);
        var sink = new ThrottleOnceSink(TimeSpan.FromMilliseconds(300));

        await using var harness = await FlusherHarness.StartAsync(options, sink, [Message("first")]);

        await harness.WaitForDrainAsync();

        sink.GapBetweenAttempts.ShouldNotBeNull();
        sink.GapBetweenAttempts!.Value.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300));
        harness.Counters.ThrottledFlushes.ShouldBe(1);
        harness.Counters.ForwardedMessages.ShouldBe(1);
        harness.Buffer.Snapshot.Depth.ShouldBe(0);
    }

    [Fact]
    public async Task RateLimit_HoldsTheSecondBatchAndCountsTheWait()
    {
        // Một message mỗi giây với burst bằng một: batch đầu tiên đi qua ngay lập tức, batch thứ hai
        // thì không, và flusher phải ghi lại lý do nó chờ chứ không chỉ đơn thuần là chạy chậm.
        var options = Options();
        options.FlushMessagesPerSecond = 1;
        options.FlushBurstMessages = 1;
        var sink = new RecordingSink();

        await using var harness = await FlusherHarness.StartAsync(options, sink, [Message("first"), Message("second")]);

        await FlusherHarness.WaitForAsync(() => harness.Counters.ForwardedMessages == 1);

        // Ngắn hơn nhiều so với một giây mà message thứ hai phải chờ để có token, nên phép assert này
        // xác nhận rằng limiter đang giữ nó lại chứ không phải máy chạy chậm một cách tình cờ.
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        harness.Counters.ForwardedMessages.ShouldBe(1);
        harness.Buffer.Snapshot.Depth.ShouldBe(1);

        await harness.WaitForDrainAsync();
        harness.Counters.ForwardedMessages.ShouldBe(2);
        harness.Counters.RateLimitedFlushes.ShouldBe(1);
    }

    [Fact]
    public async Task AnIngestionTimeout_IsRetried_NotAHostStop()
    {
        // HttpClient ném TaskCanceledException khi timeout của chính nó hết hạn, và
        // TaskCanceledException lại kế thừa từ OperationCanceledException. Một catch filter viết theo
        // kiểu "is not OperationCanceledException" vì vậy sẽ để lọt mọi ingestion timeout, và host bị
        // dừng lại. Đo được trong lab §5.C10.2 trước khi điều này được chốt: sáu lần gateway restart
        // trong một sự cố backend kéo dài hai phút, và một backlog trễ mất ngân sách ba phút của nó.
        //
        // Restart edge chỉ vì backend không kết nối được chính là điều N15 cấm, và đó cũng là thất
        // bại duy nhất mà store-and-forward buffer tồn tại để ngăn chặn.
        var options = Options();
        var sink = new TimeOutOnceSink();

        await using var harness = await FlusherHarness.StartAsync(options, sink, [Message("first")]);

        await harness.WaitForDrainAsync();

        sink.MetricNames.ShouldBe(["first", "first"]);
        harness.Counters.ForwardedMessages.ShouldBe(1);
        harness.Flusher.ExecuteTask?.IsFaulted.ShouldBeFalse();
    }

    [Fact]
    public async Task RateLimitDisabled_ForwardsWithoutPacing()
    {
        var options = Options();
        options.FlushMessagesPerSecond = 0;
        var sink = new RecordingSink();

        await using var harness = await FlusherHarness.StartAsync(options, sink, [Message("first"), Message("second")]);

        await harness.WaitForDrainAsync();

        harness.Counters.ForwardedMessages.ShouldBe(2);
        harness.Counters.RateLimitedFlushes.ShouldBe(0);
    }

    private static PersistentBufferOptions Options() => new()
    {
        DirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "nvm-flusher-tests",
            Guid.NewGuid().ToString("N")),
        SegmentBytes = 1024 * 1024,
        MaxBytes = 16 * 1024 * 1024,
        MaxRecordBytes = 4096,
        FlushBatchSize = 1,
        FlushBatchBytes = 4096,
        FlushRetryDelay = TimeSpan.FromMilliseconds(1),
        FlushRetryMaxDelay = TimeSpan.FromMilliseconds(50),
        FlushMessagesPerSecond = 0,
    };

    private static DecodedSparkplugMessage Message(string metricName) =>
        SparkplugIngressBatchCodecTests.Message(
            new DeviceReading(
                metricName,
                Alias: null,
                new MetricValue.Integral(1),
                new DateTimeOffset(2026, 8, 28, 9, 15, 30, TimeSpan.Zero)));

    private sealed class FlusherHarness : IAsyncDisposable
    {
        private readonly PersistentBufferOptions _options;
        private readonly GatewayBufferMetrics _bufferMetrics;
        private readonly GatewayFlushMetrics _flushMetrics;

        private FlusherHarness(
            PersistentBufferOptions options,
            FileStoreAndForwardBuffer buffer,
            GatewayBufferMetrics bufferMetrics,
            GatewayFlushMetrics flushMetrics,
            GatewayCounters counters,
            StoreAndForwardFlusher flusher)
        {
            _options = options;
            _bufferMetrics = bufferMetrics;
            _flushMetrics = flushMetrics;
            Buffer = buffer;
            Counters = counters;
            Flusher = flusher;
        }

        internal FileStoreAndForwardBuffer Buffer { get; }

        internal GatewayCounters Counters { get; }

        internal StoreAndForwardFlusher Flusher { get; }

        internal static async Task<FlusherHarness> StartAsync(
            PersistentBufferOptions options,
            IGatewayBatchSink sink,
            DecodedSparkplugMessage[] messages)
        {
            var buffer = new FileStoreAndForwardBuffer(options);
            await buffer.AppendBatchAsync(
                [.. messages.Select(message => (ReadOnlyMemory<byte>)SparkplugIngressBatchCodec.Encode([message]))],
                TestContext.Current.CancellationToken);

            var counters = new GatewayCounters();
            var bufferMetrics = new GatewayBufferMetrics(buffer);
            var flushMetrics = new GatewayFlushMetrics(counters, TimeProvider.System);
            var flusher = new StoreAndForwardFlusher(
                buffer,
                bufferMetrics,
                flushMetrics,
                sink,
                options,
                new FlushRateLimiter(options, TimeProvider.System),
                new FlushBackoff(options, new Random(Seed: 1)),
                new EdgeGatewayOptions { Buffer = options },
                counters,
                TimeProvider.System,
                NullLogger<StoreAndForwardFlusher>.Instance);

            await flusher.StartAsync(TestContext.Current.CancellationToken);
            return new FlusherHarness(options, buffer, bufferMetrics, flushMetrics, counters, flusher);
        }

        internal Task WaitForDrainAsync() => WaitForAsync(() => Buffer.Snapshot.Depth == 0);

        internal static async Task WaitForAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }

            condition().ShouldBeTrue();
        }

        public async ValueTask DisposeAsync()
        {
            await Flusher.StopAsync(CancellationToken.None);
            Flusher.Dispose();
            _flushMetrics.Dispose();
            _bufferMetrics.Dispose();
            await Buffer.DisposeAsync();

            if (Directory.Exists(_options.DirectoryPath))
            {
                Directory.Delete(_options.DirectoryPath, recursive: true);
            }
        }
    }

    private class RecordingSink : IGatewayBatchSink
    {
        internal List<string> MetricNames { get; } = [];

        public Task SendAsync(
            IReadOnlyCollection<DecodedSparkplugMessage> messages,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MetricNames.Add(messages.Single().Readings.Single().MetricName);
            OnAttempt(MetricNames.Count);
            return Task.CompletedTask;
        }

        private protected virtual void OnAttempt(int attempt)
        {
        }
    }

    private sealed class FailFirstSink : RecordingSink
    {
        internal TaskCompletionSource ThirdCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private protected override void OnAttempt(int attempt)
        {
            if (attempt == 1)
            {
                throw new HttpRequestException("Expected first-attempt failure.");
            }

            if (attempt == 3)
            {
                ThirdCall.TrySetResult();
            }
        }
    }

    private sealed class TimeOutOnceSink : RecordingSink
    {
        private protected override void OnAttempt(int attempt)
        {
            if (attempt == 1)
            {
                // Chính xác là những gì HttpClient ném ra khi timeout của chính nó xảy ra, được bọc
                // theo đúng cách như vậy.
                throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing.",
                    new TimeoutException("A task was canceled."));
            }
        }
    }

    private sealed class ThrottleOnceSink(TimeSpan retryAfter) : RecordingSink
    {
        private readonly List<long> _attemptTimestamps = [];

        internal TimeSpan? GapBetweenAttempts => _attemptTimestamps.Count < 2
            ? null
            : TimeProvider.System.GetElapsedTime(_attemptTimestamps[0], _attemptTimestamps[1]);

        private protected override void OnAttempt(int attempt)
        {
            _attemptTimestamps.Add(TimeProvider.System.GetTimestamp());

            if (attempt == 1)
            {
                throw new GatewayBackpressureException(HttpStatusCode.TooManyRequests, retryAfter);
            }
        }
    }
}
