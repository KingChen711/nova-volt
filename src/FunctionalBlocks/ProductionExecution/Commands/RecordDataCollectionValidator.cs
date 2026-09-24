using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.Identity;

namespace Nvm.ProductionExecution.Commands;

/// <summary>Kiểm HÌNH DẠNG một request nhập kết quả đo — không đọc state, không kết luận đạt/không đạt.</summary>
/// <remarks>
/// Nằm ngoài cùng pipeline (trước claim), nên một request sai định dạng bị bác mà không chiếm khoá
/// (ADR-023). "Serial đúng 16 ký tự" và "signal/đơn vị đúng contract M4" nằm ở đây; còn "operation run
/// đúng pack này", "step là EOL", "pack không bị Held" cần đọc context và là business check trong
/// <see cref="Handlers.RecordDataCollectionHandler"/> — trả rejection 200, không phải 400.
/// </remarks>
public sealed class RecordDataCollectionValidator : ICommandValidator<RecordDataCollectionCommand>
{
    // M4 cố định một form: điện áp pack tại EOL. Signal/đơn vị là contract, sai là request hỏng (400),
    // không phải một phép đo hợp lệ bị chặn vì nghiệp vụ.
    private const string RequiredSignalCode = "PackVoltage";
    private const string RequiredUnitOfMeasure = "V";

    /// <inheritdoc />
    public IEnumerable<ValidationFailure> Validate(RecordDataCollectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.SiteId) || command.SiteId.Length != 3
            || !command.SiteId.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
        {
            yield return new ValidationFailure(nameof(command.SiteId), "Site phải là 3 ký tự chữ hoa/số.");
        }

        if (string.IsNullOrWhiteSpace(command.SubmissionId))
        {
            yield return new ValidationFailure(nameof(command.SubmissionId), "SubmissionId là bắt buộc.");
        }

        // Dùng chính parser scanner đọc lại: fixture/UI không được gửi serial mà scanner sẽ từ chối.
        if (!SerialNumber.TryParse(command.SerialNumber, out _))
        {
            yield return new ValidationFailure(
                nameof(command.SerialNumber), "Serial không đúng định dạng 16 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(command.OperationRunId) || command.OperationRunId.Length > 100)
        {
            yield return new ValidationFailure(nameof(command.OperationRunId), "OperationRunId phải có 1–100 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(command.StepCode) || command.StepCode.Length > 20)
        {
            yield return new ValidationFailure(nameof(command.StepCode), "StepCode phải có 1–20 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(command.EquipmentPath) || command.EquipmentPath.Length > 200)
        {
            yield return new ValidationFailure(nameof(command.EquipmentPath), "EquipmentPath phải có 1–200 ký tự.");
        }

        if (!string.Equals(command.SignalCode, RequiredSignalCode, StringComparison.Ordinal))
        {
            yield return new ValidationFailure(
                nameof(command.SignalCode), $"SignalCode phải là '{RequiredSignalCode}' ở M4.");
        }

        if (!string.Equals(command.UnitOfMeasure, RequiredUnitOfMeasure, StringComparison.Ordinal))
        {
            yield return new ValidationFailure(
                nameof(command.UnitOfMeasure), $"UnitOfMeasure phải là '{RequiredUnitOfMeasure}' ở M4.");
        }
    }
}
