using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nvm.Bus.Topology;

namespace Nvm.UnitTests.Bus;

public sealed class NvmRetryPolicyTests
{
    [Fact]
    public void Intervals_HasOneFewerEntryThanThereAreAttempts()
    {
        // Lỗi off-by-one khiến "retry 5 lần" biến thành sáu lần consumer chạy. Bốn khoảng chờ nằm
        // giữa năm lần thử, và MassTransit đếm theo số interval, không đếm theo số lần thử.
        NvmRetryPolicy.Intervals(new Random(1)).Length.ShouldBe(NvmRetryPolicy.MaxAttempts - 1);
    }

    [Fact]
    public void Intervals_GrowRoughlyExponentiallyAndStayInsideTheJitterBand()
    {
        var intervals = NvmRetryPolicy.Intervals(new Random(1));

        var expected = NvmRetryPolicy.FirstDelay.TotalMilliseconds;

        foreach (var interval in intervals)
        {
            var lower = expected * (1 - NvmRetryPolicy.JitterFraction);
            var upper = expected * (1 + NvmRetryPolicy.JitterFraction);

            interval.TotalMilliseconds.ShouldBeInRange(lower, upper);

            expected = Math.Min(expected * 2, NvmRetryPolicy.MaxDelay.TotalMilliseconds);
        }
    }

    [Fact]
    public void Intervals_AreNeverNegativeOrZero()
    {
        // Một jitter fraction lớn hơn 1 sẽ tạo ra thời gian chờ âm, và MassTransit sẽ chấp nhận nó.
        // Dải giá trị này hiện là một hằng số, nên đây là một lớp bảo vệ phòng ai đó nới rộng nó sau này.
        foreach (var interval in NvmRetryPolicy.Intervals(new Random(7)))
        {
            interval.ShouldBeGreaterThan(TimeSpan.Zero);
        }
    }

    [Fact]
    public void Intervals_DifferBetweenProcesses()
    {
        // Đây là toàn bộ lý do jitter tồn tại. Hai instance của service cùng hồi phục sau một sự cố
        // broker không được retry đồng bộ với nhau, nếu không làn sóng retry sẽ ập tới đúng vào lúc
        // hệ thống ít khả năng hấp thụ nó nhất.
        var first = NvmRetryPolicy.Intervals(new Random(1));
        var second = NvmRetryPolicy.Intervals(new Random(2));

        first.ShouldNotBe(second);
    }

    [Fact]
    public void Intervals_AreRepeatableForAGivenSeed()
    {
        NvmRetryPolicy.Intervals(new Random(42)).ShouldBe(NvmRetryPolicy.Intervals(new Random(42)));
    }

    [Fact]
    public async Task AFailingConsumer_RunsExactlyMaxAttemptsTimesAndThenFaults()
    {
        // Cái contract mà các interval phục vụ cho, được chứng minh end-to-end trên in-memory
        // transport của MassTransit — không cần broker. Đây chính là thứ chốt lại lỗi off-by-one: một
        // mảng bốn interval phải tạo ra năm lần thực thi, không phải bốn và không phải sáu.
        //
        // Thời gian chờ cố tình được rút ngắn. Policy thật bắt đầu ở 200 ms rồi tăng gấp đôi, nên
        // chạy nguyên bản ở đây sẽ tốn ba giây chỉ để chứng minh một điều về việc đếm số.
        var attempts = new AttemptCounter();

        await using var provider = new ServiceCollection()
            .AddSingleton(attempts)
            .AddMassTransitTestHarness(bus =>
            {
                bus.AddConsumer<AlwaysFailsProbeConsumer>();
                bus.AddConfigureEndpointsCallback((_, _, endpoint) =>
                    endpoint.UseMessageRetry(retry => retry.Intervals(
                        [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(1), NvmRetryPolicy.MaxAttempts - 1)])));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new AlwaysFailsProbe("NV1"), TestContext.Current.CancellationToken);

        (await harness.Published.Any<Fault<AlwaysFailsProbe>>(TestContext.Current.CancellationToken)).ShouldBeTrue(
            "a message that never succeeds must end up faulted, not retried forever");
        attempts.Count.ShouldBe(NvmRetryPolicy.MaxAttempts);
    }

    [Fact]
    public async Task AConsumerThatRecovers_IsNotFaulted()
    {
        // Trường hợp mà retry được sinh ra để phục vụ. Một database đang kết nối lại hay một lock bị
        // giữ trong chốc lát không nên tốn kém gì hơn một khoảng chờ ngắn — message không được phép
        // vì thế mà rơi vào error queue.
        var attempts = new AttemptCounter();

        await using var provider = new ServiceCollection()
            .AddSingleton(attempts)
            .AddMassTransitTestHarness(bus =>
            {
                bus.AddConsumer<FailsTwiceProbeConsumer>();
                bus.AddConfigureEndpointsCallback((_, _, endpoint) =>
                    endpoint.UseMessageRetry(retry => retry.Intervals(
                        [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(1), NvmRetryPolicy.MaxAttempts - 1)])));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new FailsTwiceProbe("NV1"), TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<FailsTwiceProbe>(TestContext.Current.CancellationToken)).ShouldBeTrue();
        attempts.Count.ShouldBe(3);
        (await harness.Published.Any<Fault<FailsTwiceProbe>>(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    public sealed record AlwaysFailsProbe(string SiteId);

    public sealed record FailsTwiceProbe(string SiteId);

    public sealed class AttemptCounter
    {
        private int _count;

        public int Count => _count;

        public int Next() => Interlocked.Increment(ref _count);
    }

    public sealed class AlwaysFailsProbeConsumer(AttemptCounter attempts) : IConsumer<AlwaysFailsProbe>
    {
        public Task Consume(ConsumeContext<AlwaysFailsProbe> context)
        {
            attempts.Next();

            throw new InvalidOperationException("The downstream system is not coming back.");
        }
    }

    public sealed class FailsTwiceProbeConsumer(AttemptCounter attempts) : IConsumer<FailsTwiceProbe>
    {
        public Task Consume(ConsumeContext<FailsTwiceProbe> context) =>
            attempts.Next() < 3
                ? throw new InvalidOperationException("SQL Server is failing over.")
                : Task.CompletedTask;
    }
}
