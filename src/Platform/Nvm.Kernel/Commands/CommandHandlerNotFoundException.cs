namespace Nvm.Kernel.Commands;

/// <summary>Thrown when a command is dispatched and no handler is registered for it.</summary>
/// <remarks>
/// Always a wiring mistake, never a runtime condition: either the handler's assembly was not passed
/// to the kernel registration, or the handler does not implement the interface it looks like it
/// implements. The message names the command type because that is the only thing the person reading
/// the log has to go on.
/// </remarks>
public sealed class CommandHandlerNotFoundException : InvalidOperationException
{
    /// <summary>Creates the exception for a command type with no handler.</summary>
    /// <param name="commandType">The command that could not be dispatched.</param>
    public CommandHandlerNotFoundException(Type commandType)
        : base(BuildMessage(commandType)) => CommandType = commandType;

    /// <summary>Creates the exception with a custom message.</summary>
    public CommandHandlerNotFoundException(string message)
        : base(message) => CommandType = typeof(void);

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public CommandHandlerNotFoundException(string message, Exception innerException)
        : base(message, innerException) => CommandType = typeof(void);

    /// <summary>The command type that had no handler.</summary>
    public Type CommandType { get; }

    private static string BuildMessage(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);

        return $"No handler is registered for command '{commandType.FullName}'. "
            + "Register one by passing its assembly to AddNvmKernel.";
    }
}
