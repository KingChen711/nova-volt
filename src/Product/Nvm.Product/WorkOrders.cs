using Nvm.Foundation;

namespace Nvm.Product;

/// <summary>Một lần chạy của một process. Trạng thái theo state machine dạng dữ liệu <see cref="WorkOrderStates.Machine"/>.</summary>
[Entity<ProductOperationBlock>]
public sealed class WorkOrder : Entity, IHasStatus
{
    public string? Status { get; set; }

    [Mandatory]
    public string? ProcessNId { get; set; }

    public string? BopNId { get; set; }

    public string? ProductMaterialNId { get; set; }

    /// <summary>Số lượng kế hoạch.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Số đã làm. Chỉ đổi khi lệnh ghi sản phẩm truyền phần cộng thêm.</summary>
    public decimal ProducedQuantity { get; set; }

    /// <summary>Vd <c>FlexibleBatch</c>: ghi sản phẩm theo lô, số lượng sửa được sau khi tạo.</summary>
    public string? ProductionType { get; set; }

    public DateTimeOffset? EstimatedStart { get; set; }

    public DateTimeOffset? EstimatedEnd { get; set; }
}

/// <summary>Bước của WO, sinh từ một bước của process. Trạng thái riêng: Open, Active, Partial, Complete.</summary>
[Entity<ProductOperationBlock>]
public sealed class WorkOrderOperation : Entity
{
    [Mandatory]
    public string? WorkOrderNId { get; set; }

    [Mandatory]
    public string? OperationNId { get; set; }

    public int Sequence { get; set; }

    public string? Status { get; set; }

    /// <summary>Máy đã báo bước này gần nhất.</summary>
    public string? ActualEquipmentNId { get; set; }
}

/// <summary>Máy dự kiến của một bước WO, chép từ máy được phép của bước process.</summary>
[Entity<ProductOperationBlock>]
public sealed class WorkOrderOperationEquipment : Entity
{
    [Mandatory]
    public string? WorkOrderOperationNId { get; set; }

    [Mandatory]
    public string? EquipmentNId { get; set; }
}

/// <summary>Nguyên liệu định sẵn của một bước WO, chép từ định mức của bước process.</summary>
[Entity<ProductOperationBlock>]
public sealed class WorkOrderOperationMaterial : Entity
{
    [Mandatory]
    public string? WorkOrderOperationNId { get; set; }

    [Mandatory]
    public string? MaterialNId { get; set; }

    public decimal Quantity { get; set; }

    public string? Usage { get; set; }
}

/// <summary>Sản phẩm (MTU) gắn với WO. NId = <c>&lt;WO&gt;|&lt;MTU&gt;</c>.</summary>
[Entity<ProductOperationBlock>]
public sealed class WorkOrderOutput : Entity
{
    [Mandatory]
    public string? WorkOrderNId { get; set; }

    [Mandatory]
    public string? MtuNId { get; set; }
}

/// <summary>Một lần trừ nguyên liệu cho sản phẩm của WO (As Built, thẻ Material).</summary>
[Entity<ProductOperationBlock>]
public sealed class MaterialConsumption : Entity
{
    [Mandatory]
    public string? WorkOrderNId { get; set; }

    public string? OperationNId { get; set; }

    [Mandatory]
    public string? ProductMtuNId { get; set; }

    [Mandatory]
    public string? ComponentMtuNId { get; set; }

    public decimal Quantity { get; set; }
}

[Event<ProductOperationBlock>]
[EventVersion(1)]
public sealed record WorkOrderStatusChanged(Guid WorkOrderId, string NId, string? FromStatus, string ToStatus, bool Forced)
    : IFoundationEvent;

/// <summary>Bộ trạng thái WO có sẵn của nhà cung cấp, nạp bằng dữ liệu. Phía dự án thêm trạng thái (vd Frozen) cũng bằng dữ liệu.</summary>
public static class WorkOrderStates
{
    public const string Machine = "WorkOrder";
    public const string Edit = "Edit";
    public const string New = "New";
    public const string Active = "Active";
    public const string Pause = "Pause";
    public const string Complete = "Complete";
    public const string Aborted = "Aborted";

    public static readonly IReadOnlyList<string> Statuses = [Edit, New, Active, Pause, Complete, Aborted];

    /// <summary>Đường đi mặc định; chuỗi rỗng là lúc tạo WO.</summary>
    public static readonly IReadOnlyList<(string From, string To)> Transitions =
    [
        ("", Edit), (Edit, New), (New, Edit), (New, Active), (Active, Pause), (Pause, Active), (Active, Complete),
        (Pause, Complete), (Edit, Aborted), (New, Aborted), (Active, Aborted), (Pause, Aborted),
    ];

    public static readonly IReadOnlyList<string> OperationStatuses = ["Open", "Active", "Partial", "Complete"];
}

/// <summary>Nạp bộ trạng thái WO mặc định. Chạy lại được.</summary>
[Command<ProductReferenceBlock>]
public sealed class InstallWorkOrderStates : ICommand<ProductResponse>;

internal sealed class InstallWorkOrderStatesHandler : ICommandHandler<InstallWorkOrderStates, ProductResponse>
{
    public ProductResponse Handle(InstallWorkOrderStates command, IPlatform platform)
    {
        foreach (var status in WorkOrderStates.Statuses)
        {
            var defined = platform.CallCommand<DefineStatus, FoundationResponse>(new DefineStatus { Machine = WorkOrderStates.Machine, Status = status });
            if (!defined.Succeeded)
            {
                return ProductResponse.Fail(defined.Error!.ErrorCode, defined.Error.ErrorMessage);
            }
        }

        foreach (var (from, to) in WorkOrderStates.Transitions)
        {
            var defined = platform.CallCommand<DefineTransition, FoundationResponse>(
                new DefineTransition { Machine = WorkOrderStates.Machine, FromStatus = from, ToStatus = to });
            if (!defined.Succeeded)
            {
                return ProductResponse.Fail(defined.Error!.ErrorCode, defined.Error.ErrorMessage);
            }
        }

        return new ProductResponse();
    }
}

internal static class WorkOrders
{
    public static ProductResponse? Find(IPlatform platform, string nid, out WorkOrder workOrder)
    {
        workOrder = platform.Query<WorkOrder>().SingleOrDefault(w => w.NId == nid)!;
        return workOrder is null ? ProductResponse.Fail(ProductErrors.UnknownWorkOrder, $"Work order {nid} does not exist.") : null;
    }

    /// <summary>Đổi trạng thái qua state machine (kiểm transition và role) rồi phát event sau commit.</summary>
    public static ProductResponse Change(IPlatform platform, WorkOrder workOrder, string toStatus)
    {
        var from = workOrder.Status;
        platform.ChangeStatus(workOrder, WorkOrderStates.Machine, toStatus);
        platform.RaiseEventOnCommit(new WorkOrderStatusChanged(workOrder.Id, workOrder.NId, from, toStatus, Forced: false));
        return ProductResponse.Of(workOrder);
    }
}

/// <summary>Tạo WO từ process: mỗi bước sinh một bước WO, mang máy dự kiến và nguyên liệu định sẵn. WO bắt đầu ở Edit.</summary>
[Command<ProductOperationBlock>]
public sealed class CreateWorkOrderFromProcess : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? ProcessNId { get; init; }

    public decimal Quantity { get; init; }

    public string? ProductionType { get; init; }

    public DateTimeOffset? EstimatedStart { get; init; }

    public DateTimeOffset? EstimatedEnd { get; init; }
}

internal sealed class CreateWorkOrderFromProcessHandler : ICommandHandler<CreateWorkOrderFromProcess, ProductResponse>
{
    public ProductResponse Handle(CreateWorkOrderFromProcess command, IPlatform platform)
    {
        var process = platform.ProjectionQuery<Process>().SingleOrDefault(p => p.NId == command.ProcessNId);
        if (process is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownProcess, $"Process {command.ProcessNId} does not exist.");
        }

        if (platform.Query<WorkOrder>().Any(w => w.NId == command.NId))
        {
            return ProductResponse.Fail(ProductErrors.DuplicateNId, $"Work order {command.NId} already exists.");
        }

        var workOrder = platform.Create<WorkOrder>();
        workOrder.NId = command.NId!;
        workOrder.ProcessNId = process.NId;
        workOrder.BopNId = process.BopNId;
        workOrder.ProductMaterialNId = process.ProductMaterialNId;
        workOrder.Quantity = command.Quantity;
        workOrder.ProductionType = command.ProductionType;
        workOrder.EstimatedStart = command.EstimatedStart;
        workOrder.EstimatedEnd = command.EstimatedEnd;
        WorkOrders.Change(platform, workOrder, WorkOrderStates.Edit);

        var operations = platform.ProjectionQuery<ProcessOperation>().Where(o => o.ProcessNId == process.NId).OrderBy(o => o.Sequence).ToList();
        var opNIds = operations.Select(o => o.NId).ToList();
        var machines = platform.ProjectionQuery<OperationEquipment>().Where(e => opNIds.Contains(e.ProcessOperationNId!)).ToList();
        var materials = platform.ProjectionQuery<OperationMaterial>().Where(m => opNIds.Contains(m.ProcessOperationNId!)).ToList();
        foreach (var operation in operations)
        {
            var woOperation = platform.Create<WorkOrderOperation>();
            woOperation.NId = workOrder.NId + ":" + operation.OperationNId;
            woOperation.WorkOrderNId = workOrder.NId;
            woOperation.OperationNId = operation.OperationNId;
            woOperation.Sequence = operation.Sequence;
            woOperation.Status = "Open";
            platform.Submit(woOperation);

            foreach (var machine in machines.Where(m => m.ProcessOperationNId == operation.NId))
            {
                var planned = platform.Create<WorkOrderOperationEquipment>();
                planned.NId = woOperation.NId + "|" + machine.EquipmentNId;
                planned.WorkOrderOperationNId = woOperation.NId;
                planned.EquipmentNId = machine.EquipmentNId;
                platform.Submit(planned);
            }

            foreach (var material in materials.Where(m => m.ProcessOperationNId == operation.NId))
            {
                var planned = platform.Create<WorkOrderOperationMaterial>();
                planned.NId = woOperation.NId + "|" + material.MaterialNId;
                planned.WorkOrderOperationNId = woOperation.NId;
                planned.MaterialNId = material.MaterialNId;
                planned.Quantity = material.Quantity;
                planned.Usage = material.Usage;
                platform.Submit(planned);
            }
        }

        return ProductResponse.Of(workOrder);
    }
}

/// <summary>Sửa số lượng kế hoạch và giờ dự kiến. Thuộc tính để rỗng thì giữ nguyên.</summary>
[Command<ProductOperationBlock>]
public sealed class EditWorkOrder : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    public decimal? Quantity { get; init; }

    public DateTimeOffset? EstimatedStart { get; init; }

    public DateTimeOffset? EstimatedEnd { get; init; }
}

internal sealed class EditWorkOrderHandler : ICommandHandler<EditWorkOrder, ProductResponse>
{
    public ProductResponse Handle(EditWorkOrder command, IPlatform platform)
    {
        if (WorkOrders.Find(platform, command.NId!, out var workOrder) is { } missing)
        {
            return missing;
        }

        workOrder.Quantity = command.Quantity ?? workOrder.Quantity;
        workOrder.EstimatedStart = command.EstimatedStart ?? workOrder.EstimatedStart;
        workOrder.EstimatedEnd = command.EstimatedEnd ?? workOrder.EstimatedEnd;
        platform.Submit(workOrder);
        return ProductResponse.Of(workOrder);
    }
}

/// <summary>Đưa WO vào sản xuất: Edit → New.</summary>
[Command<ProductOperationBlock>]
public sealed class ReleaseWorkOrder : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class ReleaseWorkOrderHandler : ICommandHandler<ReleaseWorkOrder, ProductResponse>
{
    public ProductResponse Handle(ReleaseWorkOrder command, IPlatform platform) =>
        WorkOrders.Find(platform, command.NId!, out var workOrder) ?? WorkOrders.Change(platform, workOrder, WorkOrderStates.New);
}

/// <summary>Đổi trạng thái WO theo transition có trong dữ liệu; transition có gán role thì kiểm role người gọi.</summary>
[Command<ProductOperationBlock>]
public sealed class SetWorkOrderStatus : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? Status { get; init; }
}

internal sealed class SetWorkOrderStatusHandler : ICommandHandler<SetWorkOrderStatus, ProductResponse>
{
    public ProductResponse Handle(SetWorkOrderStatus command, IPlatform platform) =>
        WorkOrders.Find(platform, command.NId!, out var workOrder) ?? WorkOrders.Change(platform, workOrder, command.Status!);
}

/// <summary>Ép trạng thái WO: bỏ qua transition và role. Trạng thái đích vẫn phải có trong state machine.</summary>
[Command<ProductOperationBlock>]
public sealed class SetWorkOrderStatusForced : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? Status { get; init; }
}

internal sealed class SetWorkOrderStatusForcedHandler : ICommandHandler<SetWorkOrderStatusForced, ProductResponse>
{
    public ProductResponse Handle(SetWorkOrderStatusForced command, IPlatform platform)
    {
        if (WorkOrders.Find(platform, command.NId!, out var workOrder) is { } missing)
        {
            return missing;
        }

        var status = WorkOrderStates.Machine + "." + command.Status;
        if (!platform.ProjectionQuery<StatusDefinition>().Any(s => s.NId == status))
        {
            return ProductResponse.Fail(7101, $"{WorkOrderStates.Machine}: status '{command.Status}' is not defined.");
        }

        var from = workOrder.Status;
        workOrder.Status = command.Status;
        platform.Submit(workOrder);
        platform.Tracer.Write(TraceLevel.Warning, $"Work order {workOrder.NId} forced {from} -> {command.Status} by {platform.Caller.User}");
        platform.RaiseEventOnCommit(new WorkOrderStatusChanged(workOrder.Id, workOrder.NId, from, command.Status!, Forced: true));
        return ProductResponse.Of(workOrder);
    }
}

/// <summary>Hủy WO: chuyển sang Aborted theo state machine (transition có thể gán role).</summary>
[Command<ProductOperationBlock>]
public sealed class AbortWorkOrder : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class AbortWorkOrderHandler : ICommandHandler<AbortWorkOrder, ProductResponse>
{
    public ProductResponse Handle(AbortWorkOrder command, IPlatform platform) =>
        WorkOrders.Find(platform, command.NId!, out var workOrder) ?? WorkOrders.Change(platform, workOrder, WorkOrderStates.Aborted);
}

/// <summary>Xóa mềm WO và các bước của nó. Lớp Product không chặn xóa WO đã chạy; chặn là pre-check phía dự án.</summary>
[Command<ProductOperationBlock>]
public sealed class DeleteWorkOrder : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class DeleteWorkOrderHandler : ICommandHandler<DeleteWorkOrder, ProductResponse>
{
    public ProductResponse Handle(DeleteWorkOrder command, IPlatform platform)
    {
        if (WorkOrders.Find(platform, command.NId!, out var workOrder) is { } missing)
        {
            return missing;
        }

        foreach (var operation in platform.Query<WorkOrderOperation>().Where(o => o.WorkOrderNId == workOrder.NId).ToList())
        {
            foreach (var machine in platform.Query<WorkOrderOperationEquipment>().Where(m => m.WorkOrderOperationNId == operation.NId).ToList())
            {
                platform.Delete(machine);
            }

            foreach (var material in platform.Query<WorkOrderOperationMaterial>().Where(m => m.WorkOrderOperationNId == operation.NId).ToList())
            {
                platform.Delete(material);
            }

            platform.Delete(operation);
        }

        platform.Delete(workOrder);
        return ProductResponse.Of(workOrder);
    }
}

/// <summary>
/// Ghi sản phẩm vào WO: tạo MTU rồi gắn, hoặc chỉ gắn MTU có sẵn. Số đã làm của WO chỉ đổi khi truyền
/// <see cref="QuantityIncrement"/>; lệnh không tự cộng.
/// </summary>
[Command<ProductOperationBlock>]
public sealed class RecordWorkOrderOutput : ICommand<ProductResponse>
{
    [Mandatory]
    public string? WorkOrderNId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }

    /// <summary>Bật: tạo MTU mới của sản phẩm WO. Tắt: MTU phải có sẵn.</summary>
    public bool CreateMtu { get; init; }

    /// <summary>Số lượng của MTU mới (lô).</summary>
    public decimal? Quantity { get; init; }

    public string? EquipmentNId { get; init; }

    /// <summary>Mã in trên MTU mới (serial, số lô).</summary>
    public string? Code { get; init; }

    public decimal? QuantityIncrement { get; init; }
}

internal sealed class RecordWorkOrderOutputHandler : ICommandHandler<RecordWorkOrderOutput, ProductResponse>
{
    public ProductResponse Handle(RecordWorkOrderOutput command, IPlatform platform)
    {
        if (WorkOrders.Find(platform, command.WorkOrderNId!, out var workOrder) is { } missing)
        {
            return missing;
        }

        if (command.CreateMtu)
        {
            var created = Mtus.Create(platform, command.MtuNId!, workOrder.ProductMaterialNId!, command.Quantity, command.EquipmentNId, workOrder.NId,
                command.Code);
            if (!created.Succeeded)
            {
                return created;
            }
        }
        else if (Mtus.Find(platform, command.MtuNId!, out _) is { } unknown)
        {
            return unknown;
        }

        var nid = workOrder.NId + "|" + command.MtuNId;
        if (!platform.Query<WorkOrderOutput>().Any(o => o.NId == nid))
        {
            var output = platform.Create<WorkOrderOutput>();
            output.NId = nid;
            output.WorkOrderNId = workOrder.NId;
            output.MtuNId = command.MtuNId;
            platform.Submit(output);
        }

        if (command.QuantityIncrement is { } increment)
        {
            workOrder.ProducedQuantity += increment;
            platform.Submit(workOrder);
        }

        return ProductResponse.Of(workOrder);
    }
}

/// <summary>Ghi tiến độ một bước WO: trạng thái bước và máy đã chạy.</summary>
[Command<ProductOperationBlock>]
public sealed class RecordOperationProgress : ICommand<ProductResponse>
{
    [Mandatory]
    public string? WorkOrderOperationNId { get; init; }

    /// <summary>Active, Partial hoặc Complete.</summary>
    [Mandatory]
    public string? Status { get; init; }

    public string? EquipmentNId { get; init; }
}

internal sealed class RecordOperationProgressHandler : ICommandHandler<RecordOperationProgress, ProductResponse>
{
    public ProductResponse Handle(RecordOperationProgress command, IPlatform platform)
    {
        var operation = platform.Query<WorkOrderOperation>().SingleOrDefault(o => o.NId == command.WorkOrderOperationNId);
        if (operation is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownWorkOrderOperation, $"Work order operation {command.WorkOrderOperationNId} does not exist.");
        }

        if (!WorkOrderStates.OperationStatuses.Contains(command.Status))
        {
            return ProductResponse.Fail(ProductErrors.UnknownWorkOrderOperation, $"Status {command.Status} is not a work order operation status.");
        }

        operation.Status = command.Status;
        operation.ActualEquipmentNId = command.EquipmentNId ?? operation.ActualEquipmentNId;
        platform.Submit(operation);
        return ProductResponse.Of(operation);
    }
}

/// <summary>
/// Trừ nguyên liệu cho một sản phẩm của WO: giảm số lượng lô nguyên liệu, ghi dòng Assemble vào lịch sử sản phẩm
/// (genealogy). Lô không đủ thì từ chối; nâng số lượng lô trước là việc của phía dự án.
/// </summary>
[Command<ProductOperationBlock>]
public sealed class RecordConsumption : ICommand<ProductResponse>
{
    [Mandatory]
    public string? WorkOrderNId { get; init; }

    public string? OperationNId { get; init; }

    [Mandatory]
    public string? ProductMtuNId { get; init; }

    [Mandatory]
    public string? ComponentMtuNId { get; init; }

    [Mandatory]
    public decimal? Quantity { get; init; }
}

internal sealed class RecordConsumptionHandler : ICommandHandler<RecordConsumption, ProductResponse>
{
    public ProductResponse Handle(RecordConsumption command, IPlatform platform)
    {
        if (WorkOrders.Find(platform, command.WorkOrderNId!, out var workOrder) is { } missingOrder)
        {
            return missingOrder;
        }

        if (Mtus.Find(platform, command.ProductMtuNId!, out var product) is { } missingProduct)
        {
            return missingProduct;
        }

        if (Mtus.Find(platform, command.ComponentMtuNId!, out var component) is { } missingComponent)
        {
            return missingComponent;
        }

        var quantity = command.Quantity!.Value;
        if (quantity <= 0)
        {
            return ProductResponse.Fail(ProductErrors.InvalidQuantity, "Consumed quantity must be positive.");
        }

        if (component.Quantity < quantity)
        {
            return ProductResponse.Fail(ProductErrors.InsufficientQuantity,
                $"MTU {component.NId} has {History.Format(component.Quantity)}, cannot consume {History.Format(quantity)}.");
        }

        component.Quantity -= quantity;
        platform.Submit(component);

        var consumption = platform.Create<MaterialConsumption>();
        consumption.NId = consumption.Id.ToString("N");
        consumption.WorkOrderNId = workOrder.NId;
        consumption.OperationNId = command.OperationNId;
        consumption.ProductMtuNId = product.NId;
        consumption.ComponentMtuNId = component.NId;
        consumption.Quantity = quantity;
        platform.Submit(consumption);

        History.Write(platform, product.NId, MtuHistoryActions.Assemble, quantity: quantity, component: component.NId, workOrder: workOrder.NId);
        History.Write(platform, component.NId, MtuHistoryActions.Edit, $"Consumed {History.Format(quantity)} into {product.NId}", component.Quantity,
            workOrder: workOrder.NId);
        return ProductResponse.Of(consumption);
    }
}
