namespace Nvm.Kernel.Commands;

/// <summary>Calls the next stage of the pipeline, ending at the handler itself.</summary>
/// <typeparam name="TResult">What the command yields.</typeparam>
public delegate Task<TResult> CommandPipelineStep<TResult>();

/// <summary>
/// A concern that wraps every command instead of being remembered inside each handler.
/// </summary>
/// <typeparam name="TCommand">The command being handled.</typeparam>
/// <typeparam name="TResult">What handling it yields.</typeparam>
/// <remarks>
/// <para>
/// Validation, deduplication and auditing apply to all forty-odd commands this system will grow. Left
/// to the handlers, they get written thirty-nine times and forgotten once — and the one forgotten is
/// where the defect lives. Here they are written once and cannot be skipped.
/// </para>
/// <para>
/// Order is a correctness property, not a preference. Behaviours run in registration order, outermost
/// first, and each decides whether to call the continuation. See
/// <see cref="KernelServiceCollectionExtensions.AddNvmKernel"/> for the order and why it is that one.
/// </para>
/// </remarks>
public interface ICommandBehavior<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>Runs this stage, calling <paramref name="continuation"/> to continue.</summary>
    /// <param name="command">The command travelling through the pipeline.</param>
    /// <param name="continuation">The rest of the pipeline. Not calling it stops the command here.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task<TResult> HandleAsync(
        TCommand command,
        CommandPipelineStep<TResult> continuation,
        CancellationToken cancellationToken);
}
