using System.Collections.Concurrent;

namespace Nvm.Kernel.Commands;

/// <summary>Resolves the handler for a command from the container and invokes it.</summary>
/// <param name="services">Scope the handler is resolved from.</param>
/// <remarks>
/// <para>
/// Takes <see cref="IServiceProvider"/> rather than a container-specific abstraction, which keeps
/// this project free of any dependency injection package (AGENTS.md K9). Only the registration
/// helper needs one.
/// </para>
/// <para>
/// The pipeline behaviours are not here yet. When they arrive they wrap this call rather than replace
/// it, so the seam stays where it is.
/// </para>
/// </remarks>
public sealed class CommandDispatcher(IServiceProvider services) : ICommandDispatcher
{
    // The command's runtime type is only known at the call site, so reaching the handler means
    // closing two generic parameters at run time. Doing that on every dispatch would put reflection
    // on the hot path of every operation on the shop floor, so each command type is reflected over
    // once and the closed invoker is kept.
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

    /// <summary>Non-generic-over-command view of a handler call, so invokers can be cached by type.</summary>
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
            var handler = services.GetService(typeof(ICommandHandler<TCommand, TResult>))
                as ICommandHandler<TCommand, TResult>
                ?? throw new CommandHandlerNotFoundException(typeof(TCommand));

            return handler.HandleAsync((TCommand)command, cancellationToken);
        }
    }
}
