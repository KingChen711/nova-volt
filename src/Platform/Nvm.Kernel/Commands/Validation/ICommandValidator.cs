namespace Nvm.Kernel.Commands.Validation;

/// <summary>One thing wrong with a command, and which field it is wrong on.</summary>
/// <param name="Field">The property at fault, so an operator screen can point at it.</param>
/// <param name="Message">What is wrong, phrased for whoever has to fix it.</param>
public sealed record ValidationFailure(string Field, string Message);

/// <summary>
/// Checks that a command is well formed, before anything acts on it.
/// </summary>
/// <typeparam name="TCommand">The command checked.</typeparam>
/// <remarks>
/// <para>
/// Shape only: required fields present, numbers in range, codes matching their format. Deliberately
/// synchronous, because that is the line between this and business rules. "Revision must be at least
/// 1" belongs here; "revision must be higher than the one currently in force" needs to read state and
/// belongs in the handler, where it can be decided inside the same transaction as the change it
/// guards.
/// </para>
/// <para>
/// A command with no validator registered passes straight through. That is a deliberate default: most
/// commands are records whose constructor already refuses nonsense, and demanding an empty validator
/// for each of them teaches people to write empty validators.
/// </para>
/// </remarks>
public interface ICommandValidator<in TCommand>
    where TCommand : ICommand
{
    /// <summary>Returns everything wrong with the command. Empty means valid.</summary>
    /// <param name="command">The command to check.</param>
    IEnumerable<ValidationFailure> Validate(TCommand command);
}
