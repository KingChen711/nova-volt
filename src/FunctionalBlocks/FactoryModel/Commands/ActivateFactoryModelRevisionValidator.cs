using Nvm.Kernel.Commands.Validation;

namespace Nvm.FactoryModel.Commands;

/// <summary>Checks the shape of an activation request, and nothing about the plant.</summary>
/// <remarks>
/// The dividing line drawn on <see cref="ICommandValidator{TCommand}"/>, in practice. "Revision must
/// be at least 1" is here, because it is true without reading anything. "That plant exists" and
/// "revision must be higher than the one in force" are in the handler, because answering them needs
/// state — and a check made against state that something else can change in the meantime has to
/// happen where the change is made, not two stages earlier.
/// </remarks>
public sealed class ActivateFactoryModelRevisionValidator : ICommandValidator<ActivateFactoryModelRevisionCommand>
{
    /// <inheritdoc />
    public IEnumerable<ValidationFailure> Validate(ActivateFactoryModelRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.SiteId))
        {
            yield return new ValidationFailure(nameof(command.SiteId), "Site is required.");
        }
        else if (!command.SiteId.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character)))
        {
            // Site codes are upper case everywhere else — in a serial number, in an equipment path, in
            // the site_id claim from Keycloak. Accepting 'nv1' here would put a second spelling into
            // the audit trail and the routing key.
            yield return new ValidationFailure(
                nameof(command.SiteId),
                $"Site '{command.SiteId}' must be upper-case letters and digits.");
        }

        if (command.Revision < 1)
        {
            yield return new ValidationFailure(
                nameof(command.Revision),
                $"Revision must be at least 1, was {command.Revision}.");
        }
    }
}
