using System.Globalization;
using System.Text.Json;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

/// <summary>
/// Production schedule: khung giờ của một ca tại một khu, kèm timer chạy mỗi ngày lúc đầu ca (<c>DAILY_WO_…</c>). Giờ ghi
/// dạng <c>HH:mm</c>; giờ kết thúc tính cả phút đó (07:00–18:59).
/// </summary>
[Entity<NvMasterBlock>]
public sealed class ProductionSchedule : Entity
{
    [Mandatory]
    public string? AreaEquipmentNId { get; set; }

    public string? Shift { get; set; }

    [Mandatory]
    public string? StartTime { get; set; }

    [Mandatory]
    public string? EndTime { get; set; }

    public string? TimerNId { get; set; }
}

/// <summary>Default Work Order: "ca này, ở khu này, cần một WO cho process này". NId = <c>&lt;PS&gt;|&lt;Process&gt;</c>.</summary>
[Entity<NvMasterBlock>]
public sealed class DefaultWorkOrder : Entity
{
    [Mandatory]
    public string? ScheduleNId { get; set; }

    [Mandatory]
    public string? ProcessNId { get; set; }
}

/// <summary>Thuộc tính lịch của BoP mà team gắn lên process của lớp Product.</summary>
[Facet<NvMasterBlock>]
public sealed class ProcessSchedule : Facet<Process>
{
    /// <summary>Tiền tố tên WO của timer, vd <c>L1</c> + phần sinh theo giờ.</summary>
    public string? WorkOrderPrefix { get; set; }

    /// <summary>WO Frozen đủ số giờ này thì tự Complete.</summary>
    public int? MinimumFreezeHours { get; set; }
}

internal static class ShiftTime
{
    public static bool TryParse(string? text, out int minutes)
    {
        minutes = 0;
        if (!TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return false;
        }

        minutes = (time.Hour * 60) + time.Minute;
        return true;
    }

    public static int Minutes(string? text) => TryParse(text, out var minutes) ? minutes : 0;

    public static int ServerOffsetMinutes(IReadOnlyPlatform platform)
    {
        var value = platform.ProjectionQuery<ConfigurationKey>().Where(k => k.NId == NvKeys.ServerUtcOffsetMinutes).Select(k => k.Val).SingleOrDefault();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) ? offset : 0;
    }

    /// <summary>Lần chạy tới của timer đầu ca: giờ bắt đầu hiểu theo giờ server, đổi sang UTC.</summary>
    public static DateTimeOffset NextStart(DateTimeOffset utcNow, int startMinutes, int offsetMinutes)
    {
        var local = utcNow.UtcDateTime.AddMinutes(offsetMinutes);
        var due = local.Date.AddMinutes(startMinutes);
        if (due <= local)
        {
            due = due.AddDays(1);
        }

        return new DateTimeOffset(due.AddMinutes(-offsetMinutes), TimeSpan.Zero);
    }

    /// <summary>Phép so khung ca bằng phút UTC của giờ hiện tại, như code của team.</summary>
    public static bool InWindow(DateTimeOffset utcNow, int start, int end)
    {
        var minute = (utcNow.UtcDateTime.Hour * 60) + utcNow.UtcDateTime.Minute;
        return start <= end ? minute >= start && minute <= end : minute >= start || minute <= end;
    }

    /// <summary>Khung ca chứa <paramref name="utcNow"/>: bắt đầu, và kết thúc ngay sau phút cuối của ca.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Window(DateTimeOffset utcNow, int start, int end)
    {
        var minute = (utcNow.UtcDateTime.Hour * 60) + utcNow.UtcDateTime.Minute;
        var day = new DateTimeOffset(utcNow.UtcDateTime.Date, TimeSpan.Zero);
        if (start > end && minute <= end)
        {
            day = day.AddDays(-1);
        }

        var length = ((end - start + 1440) % 1440) + 1;
        var from = day.AddMinutes(start);
        return (from, from.AddMinutes(length));
    }
}

/// <summary>
/// Tạo production schedule, timer hằng ngày lúc đầu ca và signal rule gọi <see cref="HandleShiftTimer"/>. Timer và rule
/// thuộc domain khác nên commit riêng ngay khi tạo.
/// </summary>
[Command<NvMasterBlock>]
public sealed class CreateProductionSchedule : ICommand<NvResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? AreaEquipmentNId { get; init; }

    public string? Shift { get; init; }

    [Mandatory]
    public string? StartTime { get; init; }

    [Mandatory]
    public string? EndTime { get; init; }
}

internal sealed class CreateProductionScheduleHandler : ICommandHandler<CreateProductionSchedule, NvResponse>
{
    public NvResponse Handle(CreateProductionSchedule command, IPlatform platform)
    {
        if (!ShiftTime.TryParse(command.StartTime, out var start) || !ShiftTime.TryParse(command.EndTime, out _))
        {
            return NvResponse.Fail(NvErrors.InvalidShiftTime, "StartTime and EndTime must be HH:mm.");
        }

        if (!platform.Query<Equipment>().Any(e => e.NId == command.AreaEquipmentNId))
        {
            return NvResponse.Fail(NvErrors.UnknownEquipment, $"Equipment {command.AreaEquipmentNId} does not exist.");
        }

        var schedule = platform.Query<ProductionSchedule>().SingleOrDefault(s => s.NId == command.NId) ?? platform.Create<ProductionSchedule>();
        schedule.NId = command.NId!;
        schedule.AreaEquipmentNId = command.AreaEquipmentNId;
        schedule.Shift = command.Shift;
        schedule.StartTime = command.StartTime;
        schedule.EndTime = command.EndTime;
        schedule.TimerNId = "DAILY_WO_" + command.NId;
        platform.Submit(schedule);

        var due = ShiftTime.NextStart(platform.Time.GetUtcNow(), start, ShiftTime.ServerOffsetMinutes(platform));
        var timer = platform.CallCommand<CreateTimer, FoundationResponse>(
            new CreateTimer { NId = schedule.TimerNId, FirstDueOn = due, IntervalSeconds = 86_400 });
        if (!timer.Succeeded)
        {
            return NvResponse.Fail(timer.Error!.ErrorCode, timer.Error.ErrorMessage);
        }

        var rule = platform.CallCommand<DefineSignalRule, FoundationResponse>(new DefineSignalRule
        {
            NId = schedule.TimerNId,
            Trigger = "Timer",
            TimerNId = schedule.TimerNId,
            App = ProductionApp.Name,
            Command = NvProductionExtApp.Name + "_" + nameof(HandleShiftTimer),
            Parameters = JsonSerializer.Serialize(new { ScheduleNId = schedule.NId }),
        });
        return rule.Succeeded ? NvResponse.Of(schedule) : NvResponse.Fail(rule.Error!.ErrorCode, rule.Error.ErrorMessage);
    }
}

[Command<NvMasterBlock>]
public sealed class CreateDefaultWorkOrder : ICommand<NvResponse>
{
    [Mandatory]
    public string? ScheduleNId { get; init; }

    [Mandatory]
    public string? ProcessNId { get; init; }
}

internal sealed class CreateDefaultWorkOrderHandler : ICommandHandler<CreateDefaultWorkOrder, NvResponse>
{
    public NvResponse Handle(CreateDefaultWorkOrder command, IPlatform platform)
    {
        if (!platform.Query<ProductionSchedule>().Any(s => s.NId == command.ScheduleNId))
        {
            return NvResponse.Fail(NvErrors.UnknownSchedule, $"Production schedule {command.ScheduleNId} does not exist.");
        }

        if (!platform.Query<Process>().Any(p => p.NId == command.ProcessNId))
        {
            return NvResponse.Fail(NvErrors.UnknownProcess, $"Process {command.ProcessNId} does not exist.");
        }

        var nid = command.ScheduleNId + "|" + command.ProcessNId;
        var dwo = platform.Query<DefaultWorkOrder>().SingleOrDefault(d => d.NId == nid) ?? platform.Create<DefaultWorkOrder>();
        dwo.NId = nid;
        dwo.ScheduleNId = command.ScheduleNId;
        dwo.ProcessNId = command.ProcessNId;
        platform.Submit(dwo);
        return NvResponse.Of(dwo);
    }
}

/// <summary>Gắn thuộc tính lịch (tiền tố tên WO, số giờ đóng băng tối thiểu) lên process.</summary>
[Command<NvMasterBlock>]
public sealed class SetProcessSchedule : ICommand<NvResponse>
{
    [Mandatory]
    public string? ProcessNId { get; init; }

    public string? WorkOrderPrefix { get; init; }

    public int? MinimumFreezeHours { get; init; }
}

internal sealed class SetProcessScheduleHandler : ICommandHandler<SetProcessSchedule, NvResponse>
{
    public NvResponse Handle(SetProcessSchedule command, IPlatform platform)
    {
        var process = platform.Query<Process>().SingleOrDefault(p => p.NId == command.ProcessNId);
        if (process is null)
        {
            return NvResponse.Fail(NvErrors.UnknownProcess, $"Process {command.ProcessNId} does not exist.");
        }

        var facet = platform.Query<ProcessSchedule>().SingleOrDefault(f => f.EntityId == process.Id) ?? platform.Create<ProcessSchedule>();
        facet.EntityId = process.Id;
        facet.WorkOrderPrefix = command.WorkOrderPrefix;
        facet.MinimumFreezeHours = command.MinimumFreezeHours;
        platform.Submit(facet);
        return NvResponse.Of(process);
    }
}
