using System.Collections.Concurrent;

namespace Nvm.Kernel.Commands;

/// <summary>Resolve handler cho một command từ container rồi gọi nó.</summary>
/// <param name="services">Scope mà handler được resolve từ đó.</param>
/// <remarks>
/// <para>
/// Nhận <see cref="IServiceProvider"/> thay vì một abstraction gắn với container cụ thể, nhờ đó dự án
/// này không phụ thuộc vào bất kỳ package dependency injection nào (AGENTS.md K9). Chỉ helper đăng ký
/// mới cần một package như vậy.
/// </para>
/// <para>
/// Behaviour bọc quanh lời gọi handler chứ không thay thế nó, và thứ tự của chúng lấy từ thứ tự chúng
/// được đăng ký. Xem <see cref="KernelServiceCollectionExtensions.AddNvmKernel"/>.
/// </para>
/// </remarks>
public sealed class CommandDispatcher(IServiceProvider services) : ICommandDispatcher
{
    // Runtime type của command chỉ được biết tại call site, nên để tới được handler cần đóng
    // (close) hai generic parameter lúc chạy. Làm việc này ở mỗi lần dispatch sẽ đặt reflection vào
    // hot path của mọi thao tác trên dây chuyền, nên mỗi command type chỉ bị reflect một lần và
    // invoker đã đóng được giữ lại để dùng tiếp.
    private static readonly ConcurrentDictionary<Type, object> Invokers = new();

    private readonly IServiceProvider _services = services;

    /// <inheritdoc />
    public Task<TResult> DispatchAsync<TResult>(
        ICommand<TResult> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var invoker = (CommandInvoker<TResult>)Invokers.GetOrAdd(
            command.GetType(),
            static commandType => CreateInvoker<TResult>(commandType));

        return invoker.InvokeAsync(command, _services, cancellationToken);
    }

    private static object CreateInvoker<TResult>(Type commandType)
    {
        var invokerType = typeof(CommandInvoker<,>).MakeGenericType(commandType, typeof(TResult));

        return Activator.CreateInstance(invokerType)
            ?? throw new CommandHandlerNotFoundException(commandType);
    }

    /// <summary>Góc nhìn không-generic-theo-command của một lời gọi handler, để invoker có thể được cache theo type.</summary>
    private abstract class CommandInvoker<TResult>
    {
        internal abstract Task<TResult> InvokeAsync(
            ICommand<TResult> command,
            IServiceProvider services,
            CancellationToken cancellationToken);
    }

    private sealed class CommandInvoker<TCommand, TResult> : CommandInvoker<TResult>
        where TCommand : ICommand<TResult>
    {
        internal override Task<TResult> InvokeAsync(
            ICommand<TResult> command,
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            var typed = (TCommand)command;

            // Được resolve trước khi bất kỳ behaviour nào chạy. Thiếu handler là lỗi wiring, và nếu
            // phát hiện ra điều đó sau khi validation và deduplication đã lên tiếng thì chỉ làm stack
            // trace dài thêm mà thôi.
            var handler = services.GetService(typeof(ICommandHandler<TCommand, TResult>))
                as ICommandHandler<TCommand, TResult>
                ?? throw new CommandHandlerNotFoundException(typeof(TCommand));

            CommandPipelineStep<TResult> next = () => handler.HandleAsync(typed, cancellationToken);

            if (services.GetService(typeof(IEnumerable<ICommandBehavior<TCommand, TResult>>))
                is not IEnumerable<ICommandBehavior<TCommand, TResult>> behaviors)
            {
                return next();
            }

            // Được bọc từ trong ra ngoài, nên behaviour đăng ký đầu tiên sẽ nằm ngoài cùng, và thứ tự
            // đăng ký trong AddNvmKernel đọc lên đúng theo thứ tự pipeline chạy.
            var ordered = behaviors as ICommandBehavior<TCommand, TResult>[] ?? [.. behaviors];

            for (var index = ordered.Length - 1; index >= 0; index--)
            {
                // Được copy vào biến local: lambda capture biến, không phải giá trị của nó, và nếu
                // dùng lại biến của vòng lặp thì mọi stage sẽ đều gọi vào stage cuối cùng được dựng.
                var behavior = ordered[index];
                var inner = next;

                next = () => behavior.HandleAsync(typed, inner, cancellationToken);
            }

            return next();
        }
    }
}
