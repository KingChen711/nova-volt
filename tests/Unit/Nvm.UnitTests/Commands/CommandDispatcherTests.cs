using Microsoft.Extensions.DependencyInjection;
using Nvm.Kernel;
using Nvm.Kernel.Commands;

namespace Nvm.UnitTests.Commands;

public sealed class CommandDispatcherTests
{
    // One key per command. Two commands sharing a natural key is a modelling fault, and the
    // idempotency store refuses it — an earlier version of this file shared one key and only found
    // out when the pipeline gained a deduplication stage.
    private static IdempotencyKey KeyFor(string command) =>
        IdempotencyKey.FromNaturalKey("NV1", "probe", command);

    private static ServiceProvider BuildContainer() =>
        new ServiceCollection()
            .AddNvmKernel(typeof(CommandDispatcherTests).Assembly)
            .BuildServiceProvider();

    [Fact]
    public async Task DispatchAsync_RegisteredCommand_ReachesItsHandlerAndReturnsTheResult()
    {
        await using var container = BuildContainer();
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.DispatchAsync(new ActivateProbe(KeyFor(nameof(ActivateProbe)), 12), TestContext.Current.CancellationToken);

        result.ShouldBe("activated:12");
    }

    [Fact]
    public async Task DispatchAsync_TwoCommandTypes_EachReachesItsOwnHandler()
    {
        // The dispatcher caches an invoker per command type. This is the assertion that the cache is
        // keyed correctly: get it wrong and the second command silently runs the first one's handler.
        await using var container = BuildContainer();
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var text = await dispatcher.DispatchAsync(new ActivateProbe(KeyFor(nameof(ActivateProbe)), 7), TestContext.Current.CancellationToken);
        var number = await dispatcher.DispatchAsync(new CountProbe(KeyFor(nameof(CountProbe))), TestContext.Current.CancellationToken);

        text.ShouldBe("activated:7");
        number.ShouldBe(42);
    }

    [Fact]
    public async Task DispatchAsync_CommandWithNoHandler_ThrowsNamingTheCommand()
    {
        // Always a wiring mistake, so the message has to say which command, or the person reading the
        // log at 3 a.m. has nothing to go on.
        await using var container = BuildContainer();
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<CommandHandlerNotFoundException>(
            () => dispatcher.DispatchAsync(new OrphanProbe(KeyFor(nameof(OrphanProbe))), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(nameof(OrphanProbe));
        thrown.Message.ShouldContain(nameof(KernelServiceCollectionExtensions.AddNvmKernel));
    }

    [Fact]
    public async Task DispatchAsync_CancelledToken_ReachesTheHandler()
    {
        // CA2016 is a warning in this repo, but a token that is passed and then ignored still compiles.
        // Formation runs take hours and hold cascades touch thousands of packs; a command that cannot
        // be cancelled is a command that keeps a shutdown waiting.
        await using var container = BuildContainer();
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync(new CancellableProbe(KeyFor(nameof(CancellableProbe))), cancelled.Token));
    }

    [Fact]
    public async Task DispatchAsync_ContainerWithScopeValidation_StillReachesTheHandler()
    {
        // ValidateScopes is what a real ASP.NET Core host turns on in Development, and BuildContainer
        // above does not. That gap hid a wiring fault for two commits: a singleton dispatcher holds
        // the root provider, and the root provider refuses to hand out a scoped handler. Every test
        // here passed, and the first dispatch inside the host threw
        // "Cannot resolve scoped service ... from root provider".
        //
        // ValidateScopes only. ValidateOnBuild would also be realistic but it walks every registration
        // in this assembly, including handlers other tests register their own fixtures for — it fails
        // here for a reason that has nothing to do with what is being asserted.
        await using var container = new ServiceCollection()
            .AddNvmKernel(typeof(CommandDispatcherTests).Assembly)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.DispatchAsync(
            new ActivateProbe(KeyFor(nameof(ActivateProbe)), 3),
            TestContext.Current.CancellationToken);

        result.ShouldBe("activated:3");
    }

    public sealed record ActivateProbe(IdempotencyKey IdempotencyKey, int Revision) : ICommand<string>;

    public sealed record CountProbe(IdempotencyKey IdempotencyKey) : ICommand<int>;

    public sealed record CancellableProbe(IdempotencyKey IdempotencyKey) : ICommand<string>;

    /// <summary>Deliberately has no handler.</summary>
    public sealed record OrphanProbe(IdempotencyKey IdempotencyKey) : ICommand<string>;

    public sealed class ActivateProbeHandler : ICommandHandler<ActivateProbe, string>
    {
        public Task<string> HandleAsync(ActivateProbe command, CancellationToken cancellationToken) =>
            Task.FromResult($"activated:{command.Revision}");
    }

    public sealed class CountProbeHandler : ICommandHandler<CountProbe, int>
    {
        public Task<int> HandleAsync(CountProbe command, CancellationToken cancellationToken) =>
            Task.FromResult(42);
    }

    public sealed class CancellableProbeHandler : ICommandHandler<CancellableProbe, string>
    {
        public Task<string> HandleAsync(CancellableProbe command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("never reached");
        }
    }
}
