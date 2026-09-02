using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Audit;
using Nvm.Kernel.Commands.Idempotency;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.UnitTests.Commands;

public sealed class CommandPipelineTests
{
    private static readonly DateTimeOffset ShiftAStart = new(2026, 8, 25, 6, 0, 0, TimeSpan.FromHours(7));

    private static ServiceProvider BuildContainer(FakeTimeProvider clock) =>
        new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<CountingHandlerState>()
            .AddNvmKernel(typeof(CommandPipelineTests).Assembly)
            .BuildServiceProvider();

    private static FakeTimeProvider NewClock() => new(ShiftAStart);

    [Fact]
    public async Task SameCommandTwice_RunsTheHandlerOnceAndReplaysTheFirstResult()
    {
        // Toàn bộ lý do pipeline này tồn tại. Operator bấm "complete step", mạng chập chờn, client
        // gửi lại. Chạy hai lần sẽ complete step hai lần và tiêu tốn material lot hai lần, số liệu sẽ
        // sai mà chẳng có gì để giải trình.
        await using var container = BuildContainer(NewClock());
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var handler = container.GetRequiredService<CountingHandlerState>();
        var command = new CompleteStep(IdempotencyKey.FromNaturalKey("NV1", "NV1CL16238A00123", "STACK"), "STACK");

        var first = await dispatcher.DispatchAsync(command, TestContext.Current.CancellationToken);
        var second = await dispatcher.DispatchAsync(command, TestContext.Current.CancellationToken);

        handler.Invocations.ShouldBe(1);
        second.ShouldBe(first);
    }

    [Fact]
    public async Task DifferentCommandsWithDifferentKeys_BothRun()
    {
        await using var container = BuildContainer(NewClock());
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var handler = container.GetRequiredService<CountingHandlerState>();

        await dispatcher.DispatchAsync(
            new CompleteStep(IdempotencyKey.FromNaturalKey("NV1", "NV1CL16238A00123", "STACK"), "STACK"),
            TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync(
            new CompleteStep(IdempotencyKey.FromNaturalKey("NV1", "NV1CL16238A00124", "STACK"), "STACK"),
            TestContext.Current.CancellationToken);

        handler.Invocations.ShouldBe(2);
    }

    [Fact]
    public async Task InvalidCommand_IsRejectedAndLeavesNoTraceInTheIdempotencyStore()
    {
        // Bằng chứng về thứ tự. Nếu validation chạy sau deduplication, key của command sai đã bị
        // đánh dấu là handled — và lần gửi lại đã được sửa, mang cùng natural key, sẽ bị nuốt như một
        // duplicate. Operator sửa form, bấm submit, thấy thành công, nhưng chẳng có gì xảy ra.
        await using var container = BuildContainer(NewClock());
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var store = (InMemoryIdempotencyStore)container.GetRequiredService<IIdempotencyStore>();
        var handler = container.GetRequiredService<CountingHandlerState>();

        await Should.ThrowAsync<CommandValidationException>(
            () => dispatcher.DispatchAsync(
                new CompleteStep(IdempotencyKey.FromNaturalKey("NV1", "x", "STACK"), StepCode: ""),
                TestContext.Current.CancellationToken));

        store.Count.ShouldBe(0);
        handler.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task InvalidCommand_ReportsEveryFailureNotJustTheFirst()
    {
        await using var container = BuildContainer(NewClock());
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<CommandValidationException>(
            () => dispatcher.DispatchAsync(
                new CompleteStep(IdempotencyKey.FromNaturalKey("NV1", "x", "STACK"), StepCode: "lowercase"),
                TestContext.Current.CancellationToken));

        thrown.Failures.Count.ShouldBe(2);
        thrown.Failures.Select(failure => failure.Field).ShouldContain(nameof(CompleteStep.StepCode));
    }

    [Fact]
    public async Task FailingHandler_LeavesTheKeyUnseenSoARetryCanStillSucceed()
    {
        // Một handler ném exception coi như chưa từng xảy ra. Ghi lại key của nó sẽ biến một lỗi tạm
        // thời thành mất dữ liệu vĩnh viễn: lần retry sẽ bị nhầm là duplicate và âm thầm không làm gì
        // cả.
        await using var container = BuildContainer(NewClock());
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var store = (InMemoryIdempotencyStore)container.GetRequiredService<IIdempotencyStore>();
        var command = new FailOnce(IdempotencyKey.FromNaturalKey("NV1", "flaky"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(command, TestContext.Current.CancellationToken));
        store.Count.ShouldBe(0);

        var recovered = await dispatcher.DispatchAsync(command, TestContext.Current.CancellationToken);

        recovered.ShouldBe("succeeded on attempt 2");
        store.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Audit_RecordsTheFakeClockAndNotTheRealOne()
    {
        // AGENTS.md K1. Một pipeline đọc DateTimeOffset.UtcNow vẫn sẽ pass mọi test khác ở đây, và
        // khiến aging saga không thể test được sau bốn milestone nữa.
        var clock = NewClock();
        await using var container = BuildContainer(clock);
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var sink = (InMemoryCommandAuditSink)container.GetRequiredService<ICommandAuditSink>();

        clock.Advance(TimeSpan.FromHours(3));
        await dispatcher.DispatchAsync(
            new CompleteStep(IdempotencyKey.FromNaturalKey("NV1", "audit", "STACK"), "STACK"),
            TestContext.Current.CancellationToken);

        var entry = sink.Entries.ShouldHaveSingleItem();
        entry.StartedAt.ShouldBe(ShiftAStart.AddHours(3));
        entry.Succeeded.ShouldBeTrue();
        entry.CommandType.ShouldBe(nameof(CompleteStep));
    }

    [Fact]
    public async Task Audit_RecordsFailuresToo()
    {
        // Một audit trail chỉ ghi lại thành công thì không thể trả lời câu hỏi mà một cuộc điều tra
        // bắt đầu bằng.
        await using var container = BuildContainer(NewClock());
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var sink = (InMemoryCommandAuditSink)container.GetRequiredService<ICommandAuditSink>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(
                new FailOnce(IdempotencyKey.FromNaturalKey("NV1", "audit-failure")),
                TestContext.Current.CancellationToken));

        var entry = sink.Entries.ShouldHaveSingleItem();
        entry.Succeeded.ShouldBeFalse();
        entry.FailureType.ShouldBe(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task Audit_DoesNotRecordASuppressedDuplicate()
    {
        // Hệ quả của việc đặt audit ở trong cùng, được assert thay vì để tự phát hiện ra sau. Một
        // duplicate không thay đổi gì cả, nên trail không có gì để nói về nó; nó được đếm như một
        // metric thay vào đó.
        await using var container = BuildContainer(NewClock());
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var sink = (InMemoryCommandAuditSink)container.GetRequiredService<ICommandAuditSink>();
        var command = new CompleteStep(IdempotencyKey.FromNaturalKey("NV1", "twice", "STACK"), "STACK");

        await dispatcher.DispatchAsync(command, TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync(command, TestContext.Current.CancellationToken);

        sink.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task BehaviourOrder_IsValidationThenIdempotencyThenAudit()
    {
        // Thứ tự này là một correctness property, không phải sở thích, nên nó được assert trực tiếp
        // thay vì suy ra từ các behaviour vốn phụ thuộc vào nó.
        await using var container = BuildContainer(NewClock());
        using var scope = container.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<ICommandBehavior<CompleteStep, string>>()
            .Select(behavior => behavior.GetType().GetGenericTypeDefinition())
            .ToArray();

        behaviors.ShouldBe([
            typeof(ValidationBehavior<,>),
            typeof(IdempotencyBehavior<,>),
            typeof(AuditBehavior<,>),
        ]);
    }

    public sealed record CompleteStep(IdempotencyKey IdempotencyKey, string StepCode) : ICommand<string>;

    public sealed record FailOnce(IdempotencyKey IdempotencyKey) : ICommand<string>;

    /// <summary>Dùng chung giữa các scope để một test có thể đếm số lần handler được gọi.</summary>
    public sealed class CountingHandlerState
    {
        public int Invocations { get; private set; }

        public int Attempts { get; private set; }

        public void RecordInvocation() => Invocations++;

        public int NextAttempt() => ++Attempts;
    }

    public sealed class CompleteStepValidator : ICommandValidator<CompleteStep>
    {
        public IEnumerable<ValidationFailure> Validate(CompleteStep command)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (string.IsNullOrWhiteSpace(command.StepCode))
            {
                yield return new ValidationFailure(nameof(CompleteStep.StepCode), "Step code is required.");
            }
            else if (!command.StepCode.All(char.IsAsciiLetterUpper))
            {
                // Hai lỗi từ một giá trị sai, để test "report everything" có gì đó để báo cáo. Step
                // code được khắc bằng chữ hoa trên routing sheet.
                yield return new ValidationFailure(nameof(CompleteStep.StepCode), "Step code must be upper case.");
                yield return new ValidationFailure(nameof(CompleteStep.StepCode), "Step code is not on the routing.");
            }
        }
    }

    public sealed class CompleteStepHandler(CountingHandlerState state) : ICommandHandler<CompleteStep, string>
    {
        public Task<string> HandleAsync(CompleteStep command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            state.RecordInvocation();

            return Task.FromResult($"completed:{command.StepCode}");
        }
    }

    public sealed class FailOnceHandler(CountingHandlerState state) : ICommandHandler<FailOnce, string>
    {
        public Task<string> HandleAsync(FailOnce command, CancellationToken cancellationToken)
        {
            var attempt = state.NextAttempt();

            return attempt == 1
                ? throw new InvalidOperationException("SQL Server is not reachable.")
                : Task.FromResult($"succeeded on attempt {attempt}");
        }
    }
}
