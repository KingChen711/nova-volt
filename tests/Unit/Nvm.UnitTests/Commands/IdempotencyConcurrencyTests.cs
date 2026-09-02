using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;

namespace Nvm.UnitTests.Commands;

/// <summary>
/// Nửa phần concurrent của AGENTS.md K7. <see cref="CommandPipelineTests"/> bao phủ các duplicate đến
/// lần lượt trước sau; các test ở đây bao phủ duplicate đến cùng lúc, đúng là trường hợp mà một
/// gateway xả hết backlog thực sự tạo ra.
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

    // ── Bản thân store ───────────────────────────────────────────────────────────────────────────
    // Deterministic, không dùng thread nào cả: claim thứ hai được thực hiện trong khi claim đầu vẫn
    // đang in flight, nên phần interleaving quan trọng được ép xảy ra thay vì trông chờ vào may rủi.

    [Fact]
    public async Task SecondClaimWhileTheFirstIsInFlight_WaitsAndReplaysTheFirstResult()
    {
        // Đúng hình dạng mà một check-then-act không xử lý nổi. Với store Find/Record kiểu cũ, lệnh
        // gọi thứ hai sẽ bị miss, được cấp claim, và chạy handler lần thứ hai.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryIdempotencyStore(NewClock());
        var key = IdempotencyKey.FromNaturalKey("NV1", "in-flight");

        var first = await store.ClaimAsync<string>(key, "CompleteStep", cancellationToken);
        first.IsGranted.ShouldBeTrue();

        // Đã start, chưa await. ClaimAsync chạy đồng bộ cho tới await của nó, nên tới lúc dòng này
        // trả về một task thì nó đã phát hiện claim đang bị giữ và đứng chờ ở đó rồi.
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
        // Một handler ném exception coi như chưa từng xảy ra, nên caller đang chờ phía sau nó phải
        // được phép làm công việc đó thay vì nhận một kết quả chưa bao giờ được tạo ra.
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
        // Một handler bị treo không được phép giữ mọi duplicate của command đó chờ mãi mãi. Đồng hồ
        // là fake, nên test này assert timeout mà không cần tốn thời gian chờ timeout thật (AGENTS.md
        // K1).
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
        // Hai command cùng suy ra một natural key là lỗi modelling từ phía trên. Bắt lỗi ngay từ lúc
        // đi vào, thay vì để nó thành một lỗi cast không liên quan ở đâu đó lúc replay.
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
        // Không phải một defect cần sửa ở đây — đây chính là giới hạn, được assert để không ai đọc
        // các test ở trên rồi kết luận rằng K7 đã được đáp ứng. Hai instance đứng sau một load
        // balancer là hai store khác nhau, và một lần restart cũng là chuyện tương tự trải dài theo
        // thời gian. Giải quyết việc này cần một database (ADR-023).
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

    // ── Toàn bộ pipeline dưới thread thật ───────────────────────────────────────────────────────

    [Fact]
    public async Task ThirtyTwoCallersOneKey_RunTheHandlerOnceAndAllReceiveTheSameResult()
    {
        // Một gateway hồi phục không gửi lại một cách lịch sự từng cái một; nó xả hết buffer ra cùng
        // lúc. Mỗi caller được giữ ở một barrier trước, để cả ba mươi hai caller được thả vào
        // dispatcher cùng lúc thay vì rỉ rả từng chút.
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

        // Người thắng giờ đang ở trong handler và không thể rời đi cho tới khi được release, nên mọi
        // caller còn lại hoặc đang chờ ở claim hoặc sắp chờ. Đúng một claim tồn tại trong suốt khoảng
        // thời gian đó.
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
        // Lặp lại vì một concurrency test chỉ pass một lần chẳng chứng minh được gì về một race
        // condition; nó chỉ chứng minh rằng một cách interleaving cụ thể là ổn. Năm mươi vòng với một
        // handler có yield cũng không phải một bằng chứng, nhưng đó là khác biệt giữa một assertion
        // và một sự trùng hợp.
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
        // Đường đi khi thất bại dưới tranh chấp. Ai thắng claim thì ném exception; mọi người đang chờ
        // phía sau phải được cho đi tiếp thay vì nhận một kết quả không ai tạo ra — và vì handler chỉ
        // fail ở lần gọi đầu tiên, nên đúng một trong số họ sau đó sẽ thành công.
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

        // Một caller gặp lỗi. Những caller còn lại được release nhờ abandon và cùng chia sẻ một kết
        // quả thành công.
        results.Count(result => result is null).ShouldBe(1);
        gate.Invocations.ShouldBe(2);
        results
            .Where(result => result is not null)
            .Select(result => result!)
            .Distinct(StringComparer.Ordinal)
            .ShouldHaveSingleItem();
        store.InFlightCount.ShouldBe(0);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    public sealed record HeldStep(IdempotencyKey IdempotencyKey) : ICommand<string>;

    public sealed record YieldingStep(IdempotencyKey IdempotencyKey) : ICommand<string>;

    public sealed record FailFirstStep(IdempotencyKey IdempotencyKey) : ICommand<string>;

    /// <summary>Cho một test giữ handler mở và đếm xem nó thực sự chạy bao nhiêu lần.</summary>
    public sealed class GateState
    {
        private int _invocations;

        /// <summary>Được handler complete khi nó bắt đầu vào, để một test biết claim đang được giữ.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Được test complete để cho handler kết thúc.</summary>
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

            // Yield thread ngay bên trong claim, để khoảng hở mà một check-then-act sẽ bị mất vào đó
            // rộng hết mức mà test này có thể tạo ra mà không cần ghim đồng hồ.
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
