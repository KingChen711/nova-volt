using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Pre-check của team gắn vào command WO của lớp Product. Bật theo app trong cấu hình solution (app Production); gọi
// cùng command bằng CallCommand trong handler (vd timer đầu ca) thì không chạy.

internal static class TransitionRoles
{
    /// <summary>
    /// Bước chuyển từ trạng thái hiện tại tới <paramref name="target"/> có gán role thì người gọi phải có một trong số đó;
    /// không gán thì cho qua. Đọc bảng role theo transition của nền.
    /// </summary>
    public static PreCheckResponse Check(IReadOnlyPlatform platform, string? workOrderNId, string target, string verb)
    {
        var response = new PreCheckResponse();
        var workOrder = platform.Query<WorkOrder>().SingleOrDefault(w => w.NId == workOrderNId);
        if (workOrder is null)
        {
            return response;
        }

        var nid = StateMachines.TransitionNId(WorkOrderStates.Machine, workOrder.Status, target);
        var roles = platform.ProjectionQuery<TransitionRole>().Where(r => r.TransitionNId == nid).Select(r => r.Role!).ToList();
        if (roles.Count > 0 && !roles.Any(platform.Caller.IsInRole))
        {
            response.SetError(NvErrors.PreCheckFailed, $"User is not allowed to {verb} Work Order.");
        }

        return response;
    }
}

[PreCheck<NvOperationBlock>]
public sealed class CheckUserHasCorrectRoleToAbort : IPreCheck<AbortWorkOrder>
{
    public PreCheckResponse Check(AbortWorkOrder command, IReadOnlyPlatform platform) =>
        TransitionRoles.Check(platform, command.NId, WorkOrderStates.Aborted, "Abort");
}

[PreCheck<NvOperationBlock>]
public sealed class CheckUserHasCorrectRoleToRelease : IPreCheck<ReleaseWorkOrder>
{
    public PreCheckResponse Check(ReleaseWorkOrder command, IReadOnlyPlatform platform) =>
        TransitionRoles.Check(platform, command.NId, WorkOrderStates.New, "Release");
}

[PreCheck<NvOperationBlock>]
public sealed class CheckUserHasCorrectRoleToChangeStatus : IPreCheck<SetWorkOrderStatus>
{
    public PreCheckResponse Check(SetWorkOrderStatus command, IReadOnlyPlatform platform) =>
        TransitionRoles.Check(platform, command.NId, command.Status!, "change the status of");
}

/// <summary>Chặn kích hoạt WO khi máy dự kiến của nó đang có WO khác Active hoặc Pause cho cùng bước.</summary>
[PreCheck<NvOperationBlock>]
public sealed class PreventTwoActiveWorkOrdersOnMachine : IPreCheck<SetWorkOrderStatus>
{
    public PreCheckResponse Check(SetWorkOrderStatus command, IReadOnlyPlatform platform)
    {
        var response = new PreCheckResponse();
        if (command.Status != WorkOrderStates.Active)
        {
            return response;
        }

        var steps = platform.Query<WorkOrderOperation>().Where(o => o.WorkOrderNId == command.NId).ToList();
        var stepNIds = steps.Select(s => s.NId).ToList();
        var planned = platform.Query<WorkOrderOperationEquipment>().Where(e => stepNIds.Contains(e.WorkOrderOperationNId!)).ToList();
        string[] running = [WorkOrderStates.Active, WorkOrderStates.Pause];
        var busy = platform.Query<WorkOrder>().Where(w => w.NId != command.NId && running.Contains(w.Status!)).Select(w => w.NId).ToList();
        foreach (var machine in planned)
        {
            var operation = steps.Single(s => s.NId == machine.WorkOrderOperationNId).OperationNId;
            var otherSteps = platform.Query<WorkOrderOperation>()
                .Where(o => busy.Contains(o.WorkOrderNId!) && o.OperationNId == operation).Select(o => o.NId).ToList();
            var clash = platform.Query<WorkOrderOperationEquipment>()
                .FirstOrDefault(e => otherSteps.Contains(e.WorkOrderOperationNId!) && e.EquipmentNId == machine.EquipmentNId);
            if (clash is not null)
            {
                response.SetError(NvErrors.PreCheckFailed,
                    $"Equipment {machine.EquipmentNId} already has an active work order for operation {operation} ({clash.WorkOrderOperationNId}).");
                return response;
            }
        }

        return response;
    }
}

/// <summary>WO đã từng chạy thì không xóa được; đưa về Edit rồi mới xóa.</summary>
[PreCheck<NvOperationBlock>]
public sealed class PreventWODeletionIfStarted : IPreCheck<DeleteWorkOrder>
{
    public PreCheckResponse Check(DeleteWorkOrder command, IReadOnlyPlatform platform)
    {
        var response = new PreCheckResponse();
        var status = platform.Query<WorkOrder>().Where(w => w.NId == command.NId).Select(w => w.Status).SingleOrDefault();
        if (status is not (null or WorkOrderStates.Edit or WorkOrderStates.New))
        {
            response.SetError(NvErrors.PreCheckFailed, "Work Order has been previously activated, cannot be deleted.");
        }

        return response;
    }
}
