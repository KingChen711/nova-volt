using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.Identity;

namespace Nvm.Traceability.Commands;

public sealed class SerializeUnitValidator : ICommandValidator<SerializeUnitCommand>
{
    public IEnumerable<ValidationFailure> Validate(SerializeUnitCommand command)
    {
        foreach (var failure in UnitCommandShape.Common(command))
        { yield return failure; }
        if (!UnitCommandShape.Has(command.ProductCode, 100))
        { yield return new ValidationFailure(nameof(command.ProductCode), "ProductCode phải có 1–100 ký tự."); }
        if (!UnitCommandShape.Has(command.WorkOrderId, 100))
        { yield return new ValidationFailure(nameof(command.WorkOrderId), "WorkOrderId phải có 1–100 ký tự."); }
        if (!UnitCommandShape.Has(command.RoutingVersion, 50))
        { yield return new ValidationFailure(nameof(command.RoutingVersion), "RoutingVersion phải có 1–50 ký tự."); }
    }
}

public sealed class StartStepValidator : ICommandValidator<StartStepCommand>
{
    public IEnumerable<ValidationFailure> Validate(StartStepCommand command)
    {
        foreach (var failure in UnitCommandShape.Common(command))
        { yield return failure; }
        foreach (var failure in UnitCommandShape.Step(command.StepCode, command.OperationRunId))
        { yield return failure; }
        if (!UnitCommandShape.Has(command.EquipmentPath, 200))
        { yield return new ValidationFailure(nameof(command.EquipmentPath), "EquipmentPath phải có 1–200 ký tự."); }
    }
}

public sealed class CompleteStepValidator : ICommandValidator<CompleteStepCommand>
{
    public IEnumerable<ValidationFailure> Validate(CompleteStepCommand command)
    {
        foreach (var failure in UnitCommandShape.Common(command))
        { yield return failure; }
        foreach (var failure in UnitCommandShape.Step(command.StepCode, command.OperationRunId))
        { yield return failure; }
    }
}

public sealed class RecordMeasurementValidator : ICommandValidator<RecordMeasurementCommand>
{
    public IEnumerable<ValidationFailure> Validate(RecordMeasurementCommand command)
    {
        foreach (var failure in UnitCommandShape.Common(command))
        { yield return failure; }
        foreach (var failure in UnitCommandShape.Step(command.StepCode, command.OperationRunId))
        { yield return failure; }
        if (!UnitCommandShape.Has(command.EquipmentPath, 200))
        { yield return new ValidationFailure(nameof(command.EquipmentPath), "EquipmentPath phải có 1–200 ký tự."); }
        if (!UnitCommandShape.Has(command.SignalCode, 100))
        { yield return new ValidationFailure(nameof(command.SignalCode), "SignalCode phải có 1–100 ký tự."); }
        if (!UnitCommandShape.Has(command.UnitOfMeasure, 30))
        { yield return new ValidationFailure(nameof(command.UnitOfMeasure), "UnitOfMeasure phải có 1–30 ký tự."); }
    }
}

internal static class UnitCommandShape
{
    internal static bool Has(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    internal static IEnumerable<ValidationFailure> Common(UnitCommand command)
    {
        if (command.SiteId is not { Length: 3 } ||
            !command.SiteId.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
        { yield return new ValidationFailure(nameof(command.SiteId), "SiteId phải là 3 ký tự chữ hoa/số."); }
        if (!Has(command.ActorId, 200))
        { yield return new ValidationFailure(nameof(command.ActorId), "ActorId phải có 1–200 ký tự."); }
        if (!Has(command.SubmissionId, 190))
        { yield return new ValidationFailure(nameof(command.SubmissionId), "SubmissionId phải có 1–190 ký tự."); }
        if (!SerialNumber.TryParse(command.SerialNumber, out var serial))
        { yield return new ValidationFailure(nameof(command.SerialNumber), "Serial không đúng định dạng 16 ký tự."); }
        else if (serial.SiteCode != command.SiteId)
        { yield return new ValidationFailure(nameof(command.SerialNumber), "Serial không thuộc site của command."); }
        if (command.OccurredAt == default)
        { yield return new ValidationFailure(nameof(command.OccurredAt), "OccurredAt là bắt buộc."); }
    }

    internal static IEnumerable<ValidationFailure> Step(string? stepCode, string? operationRunId)
    {
        if (!Has(stepCode, 20))
        { yield return new ValidationFailure("StepCode", "StepCode phải có 1–20 ký tự."); }
        if (!Has(operationRunId, 100))
        { yield return new ValidationFailure("OperationRunId", "OperationRunId phải có 1–100 ký tự."); }
    }
}
