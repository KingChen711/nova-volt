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
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nvm-flusher-tests",
            Guid.NewGuid().ToString("N"));
        var options = new PersistentBufferOptions
        {
            DirectoryPath = directory,
            SegmentBytes = 1024 * 1024,
            MaxBytes = 16 * 1024 * 1024,
            MaxRecordBytes = 4096,
            FlushBatchSize = 1,
            FlushBatchBytes = 4096,
            FlushRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        try
        {
            await using var buffer = new FileStoreAndForwardBuffer(options);
            var first = Message("first");
            var second = Message("second");
            await buffer.AppendBatchAsync(
                new ReadOnlyMemory<byte>[]
                {
                    SparkplugIngressBatchCodec.Encode([first]),
                    SparkplugIngressBatchCodec.Encode([second]),
                },
                TestContext.Current.CancellationToken);

            var sink = new FailFirstSink();
            var counters = new GatewayCounters();
            using var metrics = new GatewayBufferMetrics(buffer);
            using var flusher = new StoreAndForwardFlusher(
                buffer,
                metrics,
                sink,
                options,
                new EdgeGatewayOptions { Buffer = options },
                counters,
                TimeProvider.System,
                NullLogger<StoreAndForwardFlusher>.Instance);

            await flusher.StartAsync(TestContext.Current.CancellationToken);
            await sink.ThirdCall.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            for (var attempt = 0; attempt < 100 && buffer.Snapshot.Depth != 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }

            await flusher.StopAsync(TestContext.Current.CancellationToken);

            sink.MetricNames.ShouldBe(["first", "first", "second"]);
            counters.ForwardedMessages.ShouldBe(2);
            buffer.Snapshot.Depth.ShouldBe(0);
            flusher.ExecuteTask?.IsFaulted.ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static DecodedSparkplugMessage Message(string metricName) =>
        SparkplugIngressBatchCodecTests.Message(
            new DeviceReading(
                metricName,
                Alias: null,
                new MetricValue.Integral(1),
                new DateTimeOffset(2026, 8, 28, 9, 15, 30, TimeSpan.Zero)));

    private sealed class FailFirstSink : IGatewayBatchSink
    {
        private int _calls;

        internal List<string> MetricNames { get; } = [];

        internal TaskCompletionSource ThirdCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(
            IReadOnlyCollection<DecodedSparkplugMessage> messages,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MetricNames.Add(messages.Single().Readings.Single().MetricName);
            var call = Interlocked.Increment(ref _calls);

            if (call == 1)
            {
                throw new HttpRequestException("Expected first-attempt failure.");
            }

            if (call == 3)
            {
                ThirdCall.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }
}
