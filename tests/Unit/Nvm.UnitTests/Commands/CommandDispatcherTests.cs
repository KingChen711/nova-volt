using Microsoft.Extensions.DependencyInjection;
using Nvm.Kernel;
using Nvm.Kernel.Commands;

namespace Nvm.UnitTests.Commands;

public sealed class CommandDispatcherTests
{
    // Mỗi command có một key riêng. Hai command dùng chung một natural key là lỗi modelling, và
    // idempotency store sẽ từ chối nó — một phiên bản trước của file này từng dùng chung một key và
    // chỉ phát hiện ra khi pipeline có thêm giai đoạn deduplication.
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
        // Dispatcher cache một invoker cho mỗi command type. Đây là assertion để kiểm tra cache được
        // keyed đúng: sai chỗ này thì command thứ hai sẽ âm thầm chạy handler của command đầu tiên.
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
        // Luôn là lỗi wiring, nên message phải nêu rõ command nào, nếu không người đọc log lúc 3 giờ
        // sáng sẽ chẳng có manh mối nào để bắt đầu.
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
        // CA2016 chỉ là warning trong repo này, nhưng một token được truyền vào rồi bị bỏ qua vẫn
        // compile được. Formation run kéo dài hàng giờ và hold cascade chạm tới hàng nghìn pack; một
        // command không thể cancel được là một command khiến shutdown phải chờ.
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
        // ValidateScopes là thứ mà một host ASP.NET Core thật bật lên ở Development, còn BuildContainer
        // ở trên thì không. Khoảng trống đó đã che giấu một lỗi wiring suốt hai commit: một dispatcher
        // singleton giữ root provider, và root provider từ chối cấp một scoped handler. Mọi test ở
        // đây đều pass, và lần dispatch đầu tiên bên trong host lại ném ra
        // "Cannot resolve scoped service ... from root provider".
        //
        // Chỉ ValidateScopes thôi. ValidateOnBuild cũng thực tế không kém nhưng nó duyệt qua mọi
        // registration trong assembly này, kể cả handler mà các test khác tự đăng ký fixture riêng —
        // nó fail ở đây vì một lý do chẳng liên quan gì tới điều đang được assert.
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

    /// <summary>Cố tình không có handler.</summary>
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
