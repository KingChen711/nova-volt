using System.Globalization;

namespace Nvm.Kernel.Commands.Validation;

/// <summary>Thrown when a command is refused before anything acted on it.</summary>
/// <remarks>
/// Carries every failure rather than the first one. An operator who fixes one field, resubmits, and
/// is told about the next field will stop trusting the screen by the third round.
/// </remarks>
public sealed class CommandValidationException : Exception
{
    /// <summary>Creates the exception from the failures found on a command.</summary>
    /// <param name="commandType">The command that was refused.</param>
    /// <param name="failures">Everything wrong with it.</param>
    public CommandValidationException(Type commandType, IReadOnlyList<ValidationFailure> failures)
        : base(BuildMessage(commandType, failures))
    {
        CommandType = commandType;
        Failures = failures;
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public CommandValidationException(string message)
        : base(message)
    {
        CommandType = typeof(void);
        Failures = [];
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public CommandValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        CommandType = typeof(void);
        Failures = [];
    }

    /// <summary>The command that was refused.</summary>
    public Type CommandType { get; }

    /// <summary>Everything wrong with it.</summary>
    public IReadOnlyList<ValidationFailure> Failures { get; }

    private static string BuildMessage(Type commandType, IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        ArgumentNullException.ThrowIfNull(failures);

        var detail = string.Join("; ", failures.Select(failure => $"{failure.Field}: {failure.Message}"));

        return string.Format(
            CultureInfo.InvariantCulture,
            "Command '{0}' was rejected by validation ({1} problem(s)): {2}",
            commandType.Name,
            failures.Count,
            detail);
    }
}
