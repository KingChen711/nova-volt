using Nvm.Contracts.Ports;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.MasterData.Commands;

public static class MasterDataReasonCodes
{
    public const string ItemExists = "CANONICAL_ITEM_EXISTS";
    public const string ItemNotFound = "CANONICAL_ITEM_NOT_FOUND";
    public const string AliasConflict = "ALIAS_CONFLICT";
    public const string TaskNotFound = "TASK_NOT_FOUND";
    public const string TaskNotOpen = "TASK_NOT_OPEN";
    public const string TaskNeedsMapping = "TASK_NEEDS_MAPPING";
}

/// <summary>Khai báo một mã chuẩn. Mã chuẩn tự là alias của chính nó.</summary>
public sealed record DefineCanonicalItemCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string Kind, string CanonicalId, string Name, string? BaseUom)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "DefineCanonicalItem";
    public override string CanonicalPayload => Canonical(Kind, CanonicalId, Name, BaseUom);
}

/// <summary>Ánh xạ một mã ERP về mã chuẩn, có lý do; đóng các task "mã lạ" của đúng mã đó.</summary>
public sealed record MapIdentityAliasCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string Kind, string ExternalCode, string CanonicalId, string Reason)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "MapIdentityAlias";
    public override string CanonicalPayload => Canonical(Kind, ExternalCode, CanonicalId, Reason);
}

/// <summary>Chấp nhận một sai lệch (ví dụ đơn vị) cho đúng chứng từ của task, kèm ghi chú. Không quy đổi dữ liệu.</summary>
public sealed record AcceptReconciliationTaskCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string TaskId, string Note)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "AcceptReconciliationTask";
    public override string CanonicalPayload => Canonical(TaskId, Note);
}

public sealed class MasterDataCommandValidator : ICommandValidator<DefineCanonicalItemCommand>,
    ICommandValidator<MapIdentityAliasCommand>, ICommandValidator<AcceptReconciliationTaskCommand>
{
    public IEnumerable<ValidationFailure> Validate(DefineCanonicalItemCommand command)
    {
        foreach (var failure in Kind(command.Kind).Concat(Text(command.CanonicalId, "CanonicalId", 100))
                     .Concat(Text(command.Name, "Name", 200)))
        { yield return failure; }
        if (command.Kind == IdentityKinds.Material && string.IsNullOrWhiteSpace(command.BaseUom))
        { yield return new ValidationFailure(nameof(command.BaseUom), "Vật liệu phải có đơn vị gốc."); }
        if (command.BaseUom is { Length: > 20 })
        { yield return new ValidationFailure(nameof(command.BaseUom), "Đơn vị tối đa 20 ký tự."); }
    }

    public IEnumerable<ValidationFailure> Validate(MapIdentityAliasCommand command) =>
        Kind(command.Kind).Concat(Text(command.ExternalCode, "ExternalCode", 100))
            .Concat(Text(command.CanonicalId, "CanonicalId", 100)).Concat(Text(command.Reason, "Reason", 500));

    public IEnumerable<ValidationFailure> Validate(AcceptReconciliationTaskCommand command) =>
        Text(command.TaskId, "TaskId", 20).Concat(Text(command.Note, "Note", 500));

    private static IEnumerable<ValidationFailure> Kind(string? kind) =>
        kind is IdentityKinds.Material or IdentityKinds.Product or IdentityKinds.Equipment
            ? [] : [new ValidationFailure("Kind", "Kind phải là Material, Product hoặc Equipment.")];

    private static IEnumerable<ValidationFailure> Text(string? value, string field, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? [new ValidationFailure(field, $"{field} phải có 1–{maximum} ký tự.")] : [];
}
