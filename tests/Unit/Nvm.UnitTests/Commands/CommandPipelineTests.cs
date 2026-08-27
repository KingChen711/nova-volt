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
        // The whole reason the pipeline exists. An operator taps "complete step", the network stutters,
        // the client resends. Running twice would complete the step twice and consume the material lot
        // twice, and the numbers would be wrong with nothing to show for it.
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
        // Order proof. If validation ran after deduplication, the bad command's key would already be
        // marked handled — and the corrected resend, which carries the same natural key, would be
        // swallowed as a duplicate. The operator fixes the form, presses submit, sees success, and
        // nothing happens.
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
        // A handler that threw did not happen. Recording its key would turn a transient failure into
        // permanent data loss: the retry would be mistaken for a duplicate and quietly do nothing.
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
        // AGENTS.md K1. A pipeline reading DateTimeOffset.UtcNow would still pass every other test here
        // and make the aging saga untestable four milestones from now.
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
        // A trail holding only successes cannot answer the question an investigation starts with.
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
        // The consequence of putting audit innermost, asserted rather than left to be discovered. A
        // duplicate changed nothing, so there is nothing for the trail to say about it; it is counted
        // as a metric instead.
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
        // The order is a correctness property, not a preference, so it is asserted directly instead of
        // being inferred from the behaviours that happen to depend on it.
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

    /// <summary>Shared across scopes so a test can count handler invocations.</summary>
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
                // Two failures from one bad value, so the "report everything" test has something to
                // report. Step codes are engraved in upper case on the routing sheet.
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
