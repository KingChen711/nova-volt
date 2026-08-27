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
        // The off-by-one that turns "retry 5 times" into six consumer executions. Four waits sit
        // between five attempts, and MassTransit counts the intervals, not the attempts.
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
        // A jitter fraction above 1 would produce a negative wait, and MassTransit would take it. The
        // band is a constant today, so this is a guard against somebody widening it later.
        foreach (var interval in NvmRetryPolicy.Intervals(new Random(7)))
        {
            interval.ShouldBeGreaterThan(TimeSpan.Zero);
        }
    }

    [Fact]
    public void Intervals_DifferBetweenProcesses()
    {
        // The whole reason jitter exists. Two service instances recovering from the same broker outage
        // must not retry in lockstep, or the retry wave lands together at exactly the moment the
        // system is least able to absorb it.
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
        // The contract the intervals feed into, proven end to end on MassTransit's in-memory transport
        // — no broker needed. This is what pins the off-by-one: an array of four intervals has to
        // produce five executions, not four and not six.
        //
        // Deliberately short waits. The real policy starts at 200 ms and doubles, so running it here
        // would spend three seconds proving something about counting.
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
        // The case retries exist for. A database reconnecting or a lock held for a moment should cost
        // nothing beyond a short wait — the message must not reach the error queue over it.
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
