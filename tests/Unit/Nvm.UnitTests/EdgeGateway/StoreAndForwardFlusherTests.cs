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
        // The first delay would be 5 ms on its own. Ingestion asks for 300 ms, so a gateway that
        // ignored the header would be back long before the server said it could cope.
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
        // One message per second with a burst of one: the first batch passes immediately, the
        // second cannot, and the flusher must record why it waited rather than just being slow.
        var options = Options();
        options.FlushMessagesPerSecond = 1;
        options.FlushBurstMessages = 1;
        var sink = new RecordingSink();

        await using var harness = await FlusherHarness.StartAsync(options, sink, [Message("first"), Message("second")]);

        await FlusherHarness.WaitForAsync(() => harness.Counters.ForwardedMessages == 1);

        // Far shorter than the one second the second message must wait for a token, so this asserts
        // the limiter is holding it rather than that the machine happened to be slow.
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
        // HttpClient throws TaskCanceledException when its own timeout elapses, and
        // TaskCanceledException derives from OperationCanceledException. A catch filter written as
        // "is not OperationCanceledException" therefore let every ingestion timeout through, and the
        // host stopped. Measured in lab §5.C10.2 before this held: six gateway restarts during one
        // two-minute backend outage, and a backlog that missed its three-minute budget.
        //
        // Restarting the edge because the backend is unreachable is what N15 forbids, and it is the
        // one failure the store-and-forward buffer exists to prevent.
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
                // Exactly what HttpClient raises on its own timeout, wrapped the same way.
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
