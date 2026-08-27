namespace Nvm.Kernel.Commands;

/// <summary>
/// The one piece of code that knows how to carry out <typeparamref name="TCommand"/>.
/// </summary>
/// <typeparam name="TCommand">The command handled.</typeparam>
/// <typeparam name="TResult">What handling it yields.</typeparam>
/// <remarks>
/// <para>
/// One command, one handler. This is what separates a command from an event: an event may be
/// consumed by nobody or by five services, and the publisher neither knows nor cares. A command has a
/// single addressee, and a command nobody handles is an error rather than a no-op — see
/// <see cref="CommandHandlerNotFoundException"/>.
/// </para>
/// <para>
/// Handlers hold business rules and nothing else. Validation, deduplication, auditing and transaction
/// scope are pipeline behaviours wrapped around this call, so that they are written once instead of
/// remembered forty times. That composition arrives with the behaviours themselves.
/// </para>
/// <para>
/// A handler must be safe to run twice on the same command (AGENTS.md K7). The idempotency behaviour
/// removes most repeats, but "most" is not a guarantee, and a handler that quietly assumes it runs
/// once is a handler that double-counts under exactly the conditions nobody tests.
/// </para>
/// </remarks>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>Carries out the command.</summary>
    /// <param name="command">The command to handle.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
