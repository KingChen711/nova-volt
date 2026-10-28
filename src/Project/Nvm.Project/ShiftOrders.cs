using System.Globalization;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

/// <summary>Thuộc tính ca mà team gắn lên WO của lớp Product: WO sinh từ PS nào, đóng băng lúc nào.</summary>
[Facet<NvOperationBlock>]
public sealed class WorkOrderShift : Facet<WorkOrder>
{
    public string? ScheduleNId { get; set; }

    public DateTimeOffset? FrozenOn { get; set; }
}

/// <summary>
/// Việc của timer đầu ca cho khu của <see cref="ScheduleNId"/>: (1) tự Complete WO Frozen đủ số giờ của BoP; (2) đóng băng
/// WO Active/Pause đã quá giờ kết thúc dự kiến; (3) kích hoạt WO New trong khung giờ; (4) BoP chưa có WO thì tạo, release,
/// kích hoạt, nhưng chỉ khi giờ UTC hiện tại nằm trong khung ca của PS gắn với DWO.
/// </summary>
[Command<NvOperationBlock>]
public sealed class HandleShiftTimer : ICommand<NvResponse>
{
    [Mandatory]
    public string? ScheduleNId { get; init; }
}

internal sealed class HandleShiftTimerHandler : ICommandHandler<HandleShiftTimer, NvResponse>
{
    private static readonly string[] Running = [WorkOrderStates.Active, WorkOrderStates.Pause];

    public NvResponse Handle(HandleShiftTimer command, IPlatform platform)
    {
        var schedule = platform.ProjectionQuery<ProductionSchedule>().SingleOrDefault(s => s.NId == command.ScheduleNId);
        if (schedule is null)
        {
            return NvResponse.Fail(NvErrors.UnknownSchedule, $"Production schedule {command.ScheduleNId} does not exist.");
        }

        var now = platform.Time.GetUtcNow();
        var areaSchedules = platform.ProjectionQuery<ProductionSchedule>().Where(s => s.AreaEquipmentNId == schedule.AreaEquipmentNId).ToList();
        var scheduleNIds = areaSchedules.Select(s => s.NId).ToList();
        var defaults = platform.ProjectionQuery<DefaultWorkOrder>().Where(d => scheduleNIds.Contains(d.ScheduleNId!)).ToList();
        var processNIds = defaults.Select(d => d.ProcessNId!).Distinct().ToList();
        var processes = platform.ProjectionQuery<Process>().Where(p => processNIds.Contains(p.NId)).ToList();
        var processIds = processes.Select(p => p.Id).ToList();
        var lifecycle = platform.ProjectionQuery<ProcessSchedule>().Where(f => processIds.Contains(f.EntityId)).ToList();

        foreach (var process in processes)
        {
            var settings = lifecycle.SingleOrDefault(f => f.EntityId == process.Id);
            AutoComplete(platform, process, settings?.MinimumFreezeHours ?? 24, now);
        }

        foreach (var process in processes)
        {
            Freeze(platform, process, now);
            Activate(platform, process, now);
        }

        foreach (var process in processes)
        {
            var matching = defaults.Where(d => d.ProcessNId == process.NId)
                .Select(d => areaSchedules.Single(s => s.NId == d.ScheduleNId))
                .FirstOrDefault(s => ShiftTime.InWindow(now, ShiftTime.Minutes(s.StartTime), ShiftTime.Minutes(s.EndTime)));
            if (matching is null)
            {
                platform.Tracer.Write(TraceLevel.Error, $"Matching BoP - Process relationship could not be found for {process.NId} at {now:O}.");
                continue;
            }

            CreateIfMissing(platform, process, matching, lifecycle.SingleOrDefault(f => f.EntityId == process.Id)?.WorkOrderPrefix, now);
        }

        return NvResponse.Of(schedule);
    }

    private static void AutoComplete(IPlatform platform, Process process, int hours, DateTimeOffset now)
    {
        var frozen = platform.Query<WorkOrder>().Where(w => w.ProcessNId == process.NId && w.Status == NvWorkOrderStates.Frozen).ToList();
        foreach (var workOrder in frozen)
        {
            var shift = platform.Query<WorkOrderShift>().SingleOrDefault(f => f.EntityId == workOrder.Id);
            if (shift?.FrozenOn is { } frozenOn && frozenOn.AddHours(hours) <= now)
            {
                Change(platform, workOrder, WorkOrderStates.Complete);
            }
        }
    }

    private static void Freeze(IPlatform platform, Process process, DateTimeOffset now)
    {
        var overdue = platform.Query<WorkOrder>()
            .Where(w => w.ProcessNId == process.NId && Running.Contains(w.Status!) && w.EstimatedEnd <= now).ToList();
        foreach (var workOrder in overdue)
        {
            if (Change(platform, workOrder, NvWorkOrderStates.Frozen))
            {
                var shift = platform.Query<WorkOrderShift>().SingleOrDefault(f => f.EntityId == workOrder.Id) ?? platform.Create<WorkOrderShift>();
                shift.EntityId = workOrder.Id;
                shift.FrozenOn = now;
                platform.Submit(shift);
            }
        }
    }

    private static void Activate(IPlatform platform, Process process, DateTimeOffset now)
    {
        var due = platform.Query<WorkOrder>()
            .Where(w => w.ProcessNId == process.NId && w.Status == WorkOrderStates.New && w.EstimatedStart <= now && w.EstimatedEnd > now).ToList();
        foreach (var workOrder in due)
        {
            Change(platform, workOrder, WorkOrderStates.Active);
        }
    }

    private static void CreateIfMissing(IPlatform platform, Process process, ProductionSchedule schedule, string? prefix, DateTimeOffset now)
    {
        string[] open = [WorkOrderStates.New, WorkOrderStates.Active];
        if (platform.Query<WorkOrder>().Any(w => w.ProcessNId == process.NId && open.Contains(w.Status!) && w.EstimatedEnd > now))
        {
            return;
        }

        var (start, end) = ShiftTime.Window(now, ShiftTime.Minutes(schedule.StartTime), ShiftTime.Minutes(schedule.EndTime));
        // Tên WO: tiền tố gắn trên BoP + thời điểm tạo. BoP chưa có tiền tố thì dùng tên process.
        var nid = (string.IsNullOrEmpty(prefix) ? process.NId : prefix) + now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var created = platform.CallCommand<CreateWorkOrderFromProcess, ProductResponse>(new CreateWorkOrderFromProcess
        {
            NId = nid,
            ProcessNId = process.NId,
            Quantity = 0,
            ProductionType = "FlexibleBatch",
            EstimatedStart = start,
            EstimatedEnd = end,
        });
        if (!created.Succeeded)
        {
            platform.Tracer.Write(TraceLevel.Error, $"Work order {nid} could not be created: {created.Error!.ErrorMessage}");
            return;
        }

        var shift = platform.Create<WorkOrderShift>();
        shift.EntityId = created.Id!.Value;
        shift.ScheduleNId = schedule.NId;
        platform.Submit(shift);

        foreach (var status in new[] { WorkOrderStates.New, WorkOrderStates.Active })
        {
            var changed = platform.CallCommand<SetWorkOrderStatus, ProductResponse>(new SetWorkOrderStatus { NId = nid, Status = status });
            if (!changed.Succeeded)
            {
                platform.Tracer.Write(TraceLevel.Error, $"Work order {nid} -> {status}: {changed.Error!.ErrorMessage}");
                return;
            }
        }
    }

    private static bool Change(IPlatform platform, WorkOrder workOrder, string status)
    {
        var changed = platform.CallCommand<SetWorkOrderStatus, ProductResponse>(new SetWorkOrderStatus { NId = workOrder.NId, Status = status });
        if (!changed.Succeeded)
        {
            platform.Tracer.Write(TraceLevel.Error, $"Work order {workOrder.NId} -> {status}: {changed.Error!.ErrorMessage}");
        }

        return changed.Succeeded;
    }
}
