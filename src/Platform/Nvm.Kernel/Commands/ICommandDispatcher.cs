namespace Nvm.Kernel.Commands;

/// <summary>Sends a command to its handler.</summary>
/// <remarks>
/// The seam that lets everything in between be added later without touching either side. A caller
/// asks for a command to be carried out; it does not know which class does it, and it does not know
/// what the pipeline puts around it.
/// </remarks>
public interface ICommandDispatcher
{
    /// <summary>Dispatches a command and returns what its handler produced.</summary>
    /// <typeparam name="TResult">What the command yields.</typeparam>
    /// <param name="command">The command to carry out.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    /// <exception cref="CommandHandlerNotFoundException">No handler is registered for the command.</exception>
    Task<TResult> DispatchAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
}
