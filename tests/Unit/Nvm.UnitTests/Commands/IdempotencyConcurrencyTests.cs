using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;

namespace Nvm.UnitTests.Commands;

/// <summary>
/// The concurrent half of AGENTS.md K7. <see cref="CommandPipelineTests"/> covers duplicates that
/// arrive one after another; these cover duplicates that arrive together, which is the case a gateway
/// flushing a backlog actually produces.
/// </summary>
public sealed class IdempotencyConcurrencyTests
{
    private static readonly DateTimeOffset ShiftAStart = new(2026, 8, 25, 6, 0, 0, TimeSpan.FromHours(7));

    private static FakeTimeProvider NewClock() => new(ShiftAStart);

    private static ServiceProvider BuildContainer(TimeProvider clock, GateState gate) =>
        new ServiceCollection()
            .AddSingleton(clock)
            .AddSingleton(gate)
            .AddNvmKernel(typeof(IdempotencyConcurrencyTests).Assembly)
            .BuildServiceProvider();

    // ── The store on its own ────────────────────────────────────────────────────────────────────
    // Deterministic, with no threads at all: the second claim is made while the first is still in
    // flight, so the interleaving that matters is forced rather than hoped for.

    [Fact]
    public async Task SecondClaimWhileTheFirstIsInFlight_WaitsAndReplaysTheFirstResult()
    {
        // The exact shape a check-then-act cannot handle. Under the old Find/Record store the second
        // call would have missed, been granted, and run the handler a second time.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryIdempotencyStore(NewClock());
        var key = IdempotencyKey.FromNaturalKey("NV1", "in-flight");

        var first = await store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        first.IsGranted.ShouldBeTrue();

        // Started, not awaited. ClaimAsync runs synchronously up to its await, so by the time this
        // returns a task it has already found the claim taken and parked on it.
        var second = store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        second.IsCompleted.ShouldBeFalse();

        await store.CompleteAsync(key, "completed:STACK", ShiftAStart, cancellationToken);

        var replayed = await second;
        replayed.IsGranted.ShouldBeFalse();
        replayed.Outcome.Result.ShouldBe("completed:STACK");
        replayed.Outcome.FirstHandledAt.ShouldBe(ShiftAStart);
    }

    [Fact]
    public async Task SecondClaimWhileTheFirstIsInFlight_IsGrantedWhenTheFirstGivesUp()
    {
        // A handler that threw did not happen, so the caller waiting behind it must be allowed to do
        // the work rather than be handed a result that was never produced.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryIdempotencyStore(NewClock());
        var key = IdempotencyKey.FromNaturalKey("NV1", "abandoned");

        await store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        var second = store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        second.IsCompleted.ShouldBeFalse();

        await store.AbandonAsync(key, cancellationToken);

        (await second).IsGranted.ShouldBeTrue();
    }

    [Fact]
    public async Task ClaimHeldWithoutSettling_TimesOutAndNamesTheCommandThatIsStuck()
    {
        // A handler that hangs must not park every duplicate of its command for ever. The clock is
        // fake, so this asserts the timeout without spending the timeout (AGENTS.md K1).
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = NewClock();
        var store = new InMemoryIdempotencyStore(clock, TimeSpan.FromSeconds(30));
        var key = IdempotencyKey.FromNaturalKey("NV1", "stuck");

        await store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        var waiting = store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);

        clock.Advance(TimeSpan.FromSeconds(31));

        var thrown = await Should.ThrowAsync<TimeoutException>(() => waiting);
        thrown.Message.ShouldContain(key.Value.ToString());
        thrown.Message.ShouldContain("CompleteStep");
    }

    [Fact]
    public async Task SameKeyFromADifferentCommand_IsRefusedAtClaimTime()
    {
        // Two commands deriving one natural key is a modelling fault upstream. Caught on the way in
        // now, rather than at replay time as an unrelated cast failure somewhere else.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryIdempotencyStore(NewClock());
        var key = IdempotencyKey.FromNaturalKey("NV1", "collision");

        await store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        await store.CompleteAsync(key, "completed:STACK", ShiftAStart, cancellationToken);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => store.ClaimAsync<string>(key, "QuarantineUnit", cancellationToken));

        thrown.Message.ShouldContain("CompleteStep");
        thrown.Message.ShouldContain("QuarantineUnit");
    }

    [Fact]
    public async Task SameKeyReadAsADifferentResultType_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryIdempotencyStore(NewClock());
        var key = IdempotencyKey.FromNaturalKey("NV1", "wrong-type");

        await store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        await store.CompleteAsync(key, "completed:STACK", ShiftAStart, cancellationToken);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => store.ClaimAsync<int>(key, "CompleteStep", cancellationToken));

        thrown.Message.ShouldContain(nameof(String));
        thrown.Message.ShouldContain(nameof(Int32));
    }

    [Fact]
    public async Task TwoStoreInstances_DoNotShareAClaim()
    {
        // Not a defect to be fixed here — it is the limit, asserted so that nobody reads the tests
        // above and concludes K7 is met. Two instances behind a load balancer are two stores, and a
        // restart is the same thing spread over time. Closing this needs a database (ADR-023).
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = NewClock();
        var beforeRestart = new InMemoryIdempotencyStore(clock);
        var afterRestart = new InMemoryIdempotencyStore(clock);
        var key = IdempotencyKey.FromNaturalKey("NV1", "survives-nothing");

        await beforeRestart.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        await beforeRestart.CompleteAsync(key, "completed:STACK", ShiftAStart, cancellationToken);

        var claim = await afterRestart.ClaimAsync<string>(key, "CompleteStep", cancellationToken);

        claim.IsGranted.ShouldBeTrue();
    }

    // ── The whole pipeline under real threads ───────────────────────────────────────────────────

    [Fact]
    public async Task ThirtyTwoCallersOneKey_RunTheHandlerOnceAndAllReceiveTheSameResult()
    {
        // A recovered gateway does not resend politely one at a time; it opens its buffer. Every
        // caller is parked on a barrier first, so all thirty-two are released into the dispatcher
        // together rather than trickling in.
        const int callers = 32;
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new GateState();
        await using var container = BuildContainer(NewClock(), gate);
        var store = (InMemoryIdempotencyStore)container.GetRequiredService<IIdempotencyStore>();
        var command = new HeldStep(IdempotencyKey.FromNaturalKey("NV1", "NV1CL16238A00123", "STACK"));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var arrived = new CountdownEvent(callers);

        var calls = Enumerable.Range(0, callers)
            .Select(_ => Task.Run(
                async () =>
                {
                    using var scope = container.CreateScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

                    arrived.Signal();
                    await start.Task;

                    return await dispatcher.DispatchAsync(command, cancellationToken);
                },
                cancellationToken))
            .ToArray();

        arrived.Wait(TimeSpan.FromSeconds(30), cancellationToken).ShouldBeTrue();
        start.SetResult();

        // The winner is now inside the handler and cannot leave until released, so every other caller
        // is either parked on the claim or about to be. Exactly one claim exists while that is true.
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        store.InFlightCount.ShouldBe(1);
        gate.Release.SetResult();

        var results = await Task.WhenAll(calls);

        gate.Invocations.ShouldBe(1);
        results.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem();
        store.Count.ShouldBe(1);
        store.InFlightCount.ShouldBe(0);
    }

    [Fact]
    public async Task EightCallersOneKey_FiftyRoundsRunning_NeverHandleTwice()
    {
        // Repeated because a concurrency test that passes once has proven nothing about a race; it has
        // proven that one interleaving was fine. Fifty rounds with a handler that yields is not a
        // proof either, but it is the difference between an assertion and a coincidence.
        const int rounds = 50;
        const int callers = 8;
        var cancellationToken = TestContext.Current.CancellationToken;

        for (var round = 0; round < rounds; round++)
        {
            var gate = new GateState();
            await using var container = BuildContainer(NewClock(), gate);
            var command = new YieldingStep(IdempotencyKey.FromNaturalKey("NV1", $"round-{round}"));

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var arrived = new CountdownEvent(callers);

            var calls = Enumerable.Range(0, callers)
                .Select(_ => Task.Run(
                    async () =>
                    {
                        using var scope = container.CreateScope();
                        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

                        arrived.Signal();
                        await start.Task;

                        return await dispatcher.DispatchAsync(command, cancellationToken);
                    },
                    cancellationToken))
                .ToArray();

            arrived.Wait(TimeSpan.FromSeconds(30), cancellationToken).ShouldBeTrue();
            start.SetResult();

            var results = await Task.WhenAll(calls);

            gate.Invocations.ShouldBe(1, $"round {round}");
            results.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem();
        }
    }

    [Fact]
    public async Task ConcurrentCallersWhenTheHandlerThrows_AllFailAndTheKeyIsLeftFreeForARetry()
    {
        // The failure path under contention. Whoever wins the claim throws; everyone waiting behind it
        // must be let through rather than handed a result nobody produced — and because the handler
        // fails only on its first invocation, exactly one of them then succeeds.
        const int callers = 8;
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new GateState();
        await using var container = BuildContainer(NewClock(), gate);
        var store = (InMemoryIdempotencyStore)container.GetRequiredService<IIdempotencyStore>();
        var command = new FailFirstStep(IdempotencyKey.FromNaturalKey("NV1", "flaky-storm"));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var arrived = new CountdownEvent(callers);

        var calls = Enumerable.Range(0, callers)
            .Select(_ => Task.Run(
                async () =>
                {
                    using var scope = container.CreateScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

                    arrived.Signal();
                    await start.Task;

                    try
                    {
                        return await dispatcher.DispatchAsync(command, cancellationToken);
                    }
                    catch (InvalidOperationException)
                    {
                        return null;
                    }
                },
                cancellationToken))
            .ToArray();

        arrived.Wait(TimeSpan.FromSeconds(30), cancellationToken).ShouldBeTrue();
        start.SetResult();

        var results = await Task.WhenAll(calls);

        // One caller met the failure. The rest were released by the abandon and share one success.
        results.Count(result => result is null).ShouldBe(1);
        gate.Invocations.ShouldBe(2);
        results
            .Where(result => result is not null)
            .Select(result => result!)
            .Distinct(StringComparer.Ordinal)
            .ShouldHaveSingleItem();
        store.InFlightCount.ShouldBe(0);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    public sealed record HeldStep(IdempotencyKey IdempotencyKey) : ICommand<string>;

    public sealed record YieldingStep(IdempotencyKey IdempotencyKey) : ICommand<string>;

    public sealed record FailFirstStep(IdempotencyKey IdempotencyKey) : ICommand<string>;

    /// <summary>Lets a test hold the handler open and count how often it actually ran.</summary>
    public sealed class GateState
    {
        private int _invocations;

        /// <summary>Completed by the handler as it enters, so a test knows the claim is held.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the test to let the handler finish.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Invocations => Volatile.Read(ref _invocations);

        public int Enter()
        {
            var invocation = Interlocked.Increment(ref _invocations);
            Entered.TrySetResult();

            return invocation;
        }
    }

    public sealed class HeldStepHandler(GateState gate) : ICommandHandler<HeldStep, string>
    {
        public async Task<string> HandleAsync(HeldStep command, CancellationToken cancellationToken)
        {
            var invocation = gate.Enter();
            await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return $"handled by invocation {invocation}";
        }
    }

    public sealed class YieldingStepHandler(GateState gate) : ICommandHandler<YieldingStep, string>
    {
        public async Task<string> HandleAsync(YieldingStep command, CancellationToken cancellationToken)
        {
            var invocation = gate.Enter();

            // Yields the thread inside the claim, so the window a check-then-act would lose in is as
            // wide as this test can make it without pinning the clock.
            await Task.Yield();

            return $"handled by invocation {invocation}";
        }
    }

    public sealed class FailFirstStepHandler(GateState gate) : ICommandHandler<FailFirstStep, string>
    {
        public async Task<string> HandleAsync(FailFirstStep command, CancellationToken cancellationToken)
        {
            var invocation = gate.Enter();
            await Task.Yield();

            return invocation == 1
                ? throw new InvalidOperationException("SQL Server is not reachable.")
                : $"handled by invocation {invocation}";
        }
    }
}
