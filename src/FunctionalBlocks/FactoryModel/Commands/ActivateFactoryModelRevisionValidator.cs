using Nvm.Kernel.Commands.Validation;

namespace Nvm.FactoryModel.Commands;

/// <summary>Kiểm tra hình dạng của một activation request, và không kiểm tra gì về plant cả.</summary>
/// <remarks>
/// Là ranh giới mà <see cref="ICommandValidator{TCommand}"/> vạch ra, áp dụng vào thực tế. "Revision
/// phải ít nhất là 1" nằm ở đây, vì điều đó đúng mà không cần đọc gì thêm. "Plant đó có tồn tại không"
/// và "revision phải cao hơn revision đang có hiệu lực" nằm trong handler, vì trả lời hai câu đó cần
/// đến state — và một phép kiểm nhắm vào state mà thứ khác có thể thay đổi trong lúc đó thì phải xảy
/// ra ngay tại nơi thay đổi được thực hiện, không phải sớm hơn hai bước.
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
            // Site code luôn viết hoa ở mọi nơi khác — trong serial number, trong equipment path,
            // trong claim site_id từ Keycloak. Chấp nhận 'nv1' ở đây sẽ đưa một cách viết thứ hai vào
            // audit trail và routing key.
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
