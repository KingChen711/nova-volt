using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.Identity;

namespace Nvm.Traceability.Commands;

public static class GenealogyReasonCodes
{
    public const string ParentNotFound = "PARENT_NOT_FOUND";
    public const string InvalidHierarchy = "INVALID_HIERARCHY";
    public const string AlreadyAssembled = "ALREADY_ASSEMBLED";
    public const string NotAssembled = "NOT_ASSEMBLED";
    public const string ParentMismatch = "PARENT_MISMATCH";
    public const string ParentNotUsable = "PARENT_NOT_USABLE";
}

/// <summary>Lắp unit con (<c>Serial</c>) vào cha. Con không được đang nằm trong cha khác.</summary>
public sealed record AssembleUnitCommand(
    string Site, string Actor, string Submission, string Serial, DateTimeOffset Time,
    string ParentSerialNumber, string Position, string OperationRunId)
    : UnitCommand(Site, Actor, Submission, Serial, Time)
{
    public override string CommandType => "AssembleUnit";
    public override string CanonicalPayload => Payload(ParentSerialNumber, Position, OperationRunId);
}

/// <summary>Rework: tháo unit con khỏi cha hiện tại, kèm mã lý do.</summary>
public sealed record RemoveUnitCommand(
    string Site, string Actor, string Submission, string Serial, DateTimeOffset Time,
    string ParentSerialNumber, string ReasonCode, string OperationRunId)
    : UnitCommand(Site, Actor, Submission, Serial, Time)
{
    public override string CommandType => "RemoveUnit";
    public override string CanonicalPayload => Payload(ParentSerialNumber, ReasonCode, OperationRunId);
}

/// <summary>Bút toán bù trừ khi cha đã ghi là sai; không phải thao tác tháo lắp thật.</summary>
public sealed record CorrectGenealogyCommand(
    string Site, string Actor, string Submission, string Serial, DateTimeOffset Time,
    string WrongParentSerialNumber, string CorrectParentSerialNumber, string Position, string ReasonText)
    : UnitCommand(Site, Actor, Submission, Serial, Time)
{
    public override string CommandType => "CorrectGenealogy";
    public override string CanonicalPayload =>
        Payload(WrongParentSerialNumber, CorrectParentSerialNumber, Position, ReasonText);
}

public sealed class AssembleUnitValidator : ICommandValidator<AssembleUnitCommand>
{
    public IEnumerable<ValidationFailure> Validate(AssembleUnitCommand command)
    {
        foreach (var failure in UnitCommandShape.Common(command))
        { yield return failure; }
        foreach (var failure in GenealogyShape.Parent(command, command.ParentSerialNumber, "ParentSerialNumber"))
        { yield return failure; }
        if (!UnitCommandShape.Has(command.Position, 20))
        { yield return new ValidationFailure(nameof(command.Position), "Position phải có 1–20 ký tự."); }
        if (!UnitCommandShape.Has(command.OperationRunId, 100))
        { yield return new ValidationFailure(nameof(command.OperationRunId), "OperationRunId phải có 1–100 ký tự."); }
    }
}

public sealed class RemoveUnitValidator : ICommandValidator<RemoveUnitCommand>
{
    public IEnumerable<ValidationFailure> Validate(RemoveUnitCommand command)
    {
        foreach (var failure in UnitCommandShape.Common(command))
        { yield return failure; }
        foreach (var failure in GenealogyShape.Parent(command, command.ParentSerialNumber, "ParentSerialNumber"))
        { yield return failure; }
        if (!UnitCommandShape.Has(command.ReasonCode, 50))
        { yield return new ValidationFailure(nameof(command.ReasonCode), "ReasonCode phải có 1–50 ký tự."); }
        if (!UnitCommandShape.Has(command.OperationRunId, 100))
        { yield return new ValidationFailure(nameof(command.OperationRunId), "OperationRunId phải có 1–100 ký tự."); }
    }
}

public sealed class CorrectGenealogyValidator : ICommandValidator<CorrectGenealogyCommand>
{
    public IEnumerable<ValidationFailure> Validate(CorrectGenealogyCommand command)
    {
        foreach (var failure in UnitCommandShape.Common(command))
        { yield return failure; }
        foreach (var failure in GenealogyShape.Parent(command, command.WrongParentSerialNumber, "WrongParentSerialNumber"))
        { yield return failure; }
        foreach (var failure in GenealogyShape.Parent(command, command.CorrectParentSerialNumber, "CorrectParentSerialNumber"))
        { yield return failure; }
        if (command.WrongParentSerialNumber == command.CorrectParentSerialNumber)
        { yield return new ValidationFailure(nameof(command.CorrectParentSerialNumber), "Cha đúng phải khác cha sai."); }
        if (!UnitCommandShape.Has(command.Position, 20))
        { yield return new ValidationFailure(nameof(command.Position), "Position phải có 1–20 ký tự."); }
        if (!UnitCommandShape.Has(command.ReasonText, 500))
        { yield return new ValidationFailure(nameof(command.ReasonText), "Lý do sửa phải có 1–500 ký tự."); }
    }
}

internal static class GenealogyShape
{
    internal static IEnumerable<ValidationFailure> Parent(UnitCommand command, string? parent, string field)
    {
        if (!SerialNumber.TryParse(parent, out var serial))
        { yield return new ValidationFailure(field, "Serial của cha không đúng định dạng 16 ký tự."); }
        else if (serial.SiteCode != command.SiteId)
        { yield return new ValidationFailure(field, "Cha không thuộc site của command."); }
        else if (parent == command.SerialNumber)
        { yield return new ValidationFailure(field, "Unit không thể nằm trong chính nó."); }
    }
}
