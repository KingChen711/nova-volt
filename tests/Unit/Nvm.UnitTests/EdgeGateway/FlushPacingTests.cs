using Microsoft.Extensions.Time.Testing;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Buffering;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class FlushPacingTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RateLimiter_SpendsTheBurstThenPacesOnTheSustainedRate()
    {
        var clock = new FakeTimeProvider(Start);
        var limiter = new FlushRateLimiter(
            new PersistentBufferOptions { FlushMessagesPerSecond = 100, FlushBurstMessages = 100 },
            clock);

        (await limiter.AcquireAsync(100, TestContext.Current.CancellationToken)).ShouldBe(TimeSpan.Zero);

        // Bucket đã cạn và refill ở tốc độ 100/s, nên 50 message tiếp theo tốn nửa giây.
        var pending = limiter.AcquireAsync(50, TestContext.Current.CancellationToken).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(500));

        (await pending).ShouldBe(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task RateLimiter_BatchLargerThanTheBurst_StillPasses()
    {
        // Một batch đã buffer có thể chứa nhiều message hơn hạn mức của một giây. Tính phí cả bucket
        // cho nó là cách điều tiết tốc độ; từ chối nó sẽ làm cursor kẹt tại một record mà nó không
        // bao giờ đủ khả năng chi trả, và buffer sẽ phình to cho tới khi trần dung lượng đĩa chặn
        // việc nhận MQTT lại.
        var clock = new FakeTimeProvider(Start);
        var limiter = new FlushRateLimiter(
            new PersistentBufferOptions { FlushMessagesPerSecond = 10, FlushBurstMessages = 10 },
            clock);

        (await limiter.AcquireAsync(1_000, TestContext.Current.CancellationToken)).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task RateLimiter_Disabled_NeverWaits()
    {
        var limiter = new FlushRateLimiter(
            new PersistentBufferOptions { FlushMessagesPerSecond = 0 },
            new FakeTimeProvider(Start));

        limiter.IsEnabled.ShouldBeFalse();
        (await limiter.AcquireAsync(1_000_000, TestContext.Current.CancellationToken)).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Backoff_DoublesUntilItReachesTheCeiling()
    {
        var options = new PersistentBufferOptions
        {
            FlushRetryDelay = TimeSpan.FromSeconds(1),
            FlushRetryMaxDelay = TimeSpan.FromSeconds(8),
            FlushRetryJitterFraction = 0,
        };
        var backoff = new FlushBackoff(options, new Random(Seed: 7));

        var delays = Enumerable.Range(1, 6).Select(failure => backoff.NextDelay(failure, serverHint: null));

        delays.ShouldBe(
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(8),
        ]);
    }

    [Fact]
    public void Backoff_LongOutage_StaysAtTheCeilingInsteadOfOverflowing()
    {
        var backoff = new FlushBackoff(
            new PersistentBufferOptions
            {
                FlushRetryDelay = TimeSpan.FromSeconds(2),
                FlushRetryMaxDelay = TimeSpan.FromSeconds(30),
                FlushRetryJitterFraction = 0,
            },
            new Random(Seed: 7));

        backoff.NextDelay(long.MaxValue, serverHint: null).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Backoff_JitterOnlyEverStretches()
    {
        // Jitter đối xứng, kiểu mà NvmRetryPolicy dùng, sẽ để gateway quay lại sớm hơn cả Retry-After
        // mà nó vừa được cấp. Ở đây, sàn (floor) phải luôn đúng cho mọi lần lấy mẫu.
        var options = new PersistentBufferOptions
        {
            FlushRetryDelay = TimeSpan.FromSeconds(1),
            FlushRetryMaxDelay = TimeSpan.FromSeconds(30),
            FlushRetryJitterFraction = 0.25,
        };
        var backoff = new FlushBackoff(options, new Random(Seed: 12345));
        var hint = TimeSpan.FromSeconds(4);

        var delays = Enumerable.Range(1, 200)
            .Select(_ => backoff.NextDelay(consecutiveFailures: 1, hint))
            .ToArray();

        delays.ShouldAllBe(delay => delay >= hint);
        delays.ShouldAllBe(delay => delay <= hint * 1.25);
        delays.Distinct().Count().ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Backoff_ServerHintBelowOurOwnWait_DoesNotSpeedUsUp()
    {
        var options = new PersistentBufferOptions
        {
            FlushRetryDelay = TimeSpan.FromSeconds(4),
            FlushRetryMaxDelay = TimeSpan.FromSeconds(30),
            FlushRetryJitterFraction = 0,
        };
        var backoff = new FlushBackoff(options, new Random(Seed: 7));

        backoff.NextDelay(consecutiveFailures: 1, TimeSpan.FromSeconds(1)).ShouldBe(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void FlushMetrics_ReportRateOverACompletedWindow()
    {
        var clock = new FakeTimeProvider(Start);
        var counters = new GatewayCounters();
        using var metrics = new GatewayFlushMetrics(counters, clock);

        metrics.RecordFlushed(1_000);
        clock.Advance(TimeSpan.FromSeconds(5));
        metrics.RecordFlushed(0);

        metrics.MessagesPerSecond.ShouldBe(200);
    }

    [Fact]
    public void Options_RateLimitWithoutBurst_IsRefusedAtStartup()
    {
        var options = new PersistentBufferOptions
        {
            FlushMessagesPerSecond = 5_000,
            FlushBurstMessages = 0,
        };

        Should.Throw<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Options_MaxRetryDelayBelowTheFirst_IsRefusedAtStartup()
    {
        var options = new PersistentBufferOptions
        {
            FlushRetryDelay = TimeSpan.FromSeconds(10),
            FlushRetryMaxDelay = TimeSpan.FromSeconds(1),
        };

        Should.Throw<InvalidOperationException>(options.Validate);
    }
}
