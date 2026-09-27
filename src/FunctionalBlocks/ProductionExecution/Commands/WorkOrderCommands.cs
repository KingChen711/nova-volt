using System.Collections.Immutable;
using System.Globalization;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.ProductionExecution.Commands;

public static class WorkOrderReasonCodes
{
    public const string WorkOrderExists = "WORK_ORDER_EXISTS";
    public const string WorkOrderNotFound = "WORK_ORDER_NOT_FOUND";
    public const string NotPending = "WORK_ORDER_NOT_PENDING";
    public const string StillPending = "WORK_ORDER_STILL_PENDING";
}

public static class WorkOrderStatuses
{
    public const string Released = "Released";
    public const string PendingMasterData = "PendingMasterData";
}

/// <summary>Một dòng vật liệu ERP yêu cầu, giữ nguyên mã và đơn vị ERP gửi.</summary>
public sealed record ErpMaterialRequirement(string ExternalMaterialId, decimal Quantity, string UnitOfMeasure);

/// <summary>
/// Work order nhận từ ERP. Submission là <c>{ScheduleId}/{WorkOrderId}</c>: cùng file gửi lại chỉ phát lại kết quả cũ.
/// </summary>
public sealed record ReceiveWorkOrderCommand(string Site, string Actor, DateTimeOffset Time, string ScheduleId,
    string WorkOrderId, string ExternalProductCode, DateTimeOffset? EarliestStart,
    ImmutableArray<ErpMaterialRequirement> Materials)
    : DurableCommand(Site, Actor, $"{ScheduleId}/{WorkOrderId}", Time)
{
    public override string CommandType => "ReceiveWorkOrder";
    public override string CanonicalPayload => Canonical([ScheduleId, WorkOrderId, ExternalProductCode,
        EarliestStart?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        .. Materials.Select(m => $"{m.ExternalMaterialId}:{Number(m.Quantity)}:{m.UnitOfMeasure}")]);

    public string SourceDocument => $"B2MML:{ScheduleId}/{WorkOrderId}";
}

/// <summary>Đánh giá lại work order đang chờ master data theo revision master data hiện tại.</summary>
public sealed record ReevaluateWorkOrderCommand(string Site, string Actor, DateTimeOffset Time, string WorkOrderId,
    long MasterDataRevision)
    : DurableCommand(Site, Actor, $"reevaluate/{WorkOrderId}/r{MasterDataRevision.ToString(CultureInfo.InvariantCulture)}", Time)
{
    public override string CommandType => "ReevaluateWorkOrder";
    public override string CanonicalPayload => Canonical(WorkOrderId, MasterDataRevision.ToString(CultureInfo.InvariantCulture));
}

public sealed class WorkOrderCommandValidator : ICommandValidator<ReceiveWorkOrderCommand>,
    ICommandValidator<ReevaluateWorkOrderCommand>
{
    public IEnumerable<ValidationFailure> Validate(ReceiveWorkOrderCommand command)
    {
        foreach (var failure in Text(command.ScheduleId, "ScheduleId", 50).Concat(Text(command.WorkOrderId, "WorkOrderId", 50))
                     .Concat(Text(command.ExternalProductCode, "ProductCode", 100)))
        { yield return failure; }
        if (command.Materials.IsDefault || command.Materials.Length > 500)
        { yield return new ValidationFailure(nameof(command.Materials), "Work order có tối đa 500 dòng vật liệu."); }
        else if (command.Materials.Any(m => string.IsNullOrWhiteSpace(m.ExternalMaterialId) || m.ExternalMaterialId.Length > 100
                     || m.Quantity <= 0 || string.IsNullOrWhiteSpace(m.UnitOfMeasure) || m.UnitOfMeasure.Length > 20))
        { yield return new ValidationFailure(nameof(command.Materials), "Mỗi dòng vật liệu cần mã, số lượng dương và đơn vị."); }
    }

    public IEnumerable<ValidationFailure> Validate(ReevaluateWorkOrderCommand command) =>
        Text(command.WorkOrderId, "WorkOrderId", 50);

    private static IEnumerable<ValidationFailure> Text(string? value, string field, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? [new ValidationFailure(field, $"{field} phải có 1–{maximum} ký tự.")] : [];
}
