using System.Globalization;
using System.Text;
using System.Text.Json;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Tin báo ra ngoài (R11): sản lượng và tiêu hao khi WO Complete, file scrap ngày và tháng, tin đặt bù hàng scrap. Tin tới ERP có lịch chặn:
// ngày bị chặn thì vào hàng đợi, timer mỗi giờ gửi dần khi hết chặn. Giờ xét chặn là giờ miền Đông Mỹ, không phải giờ máy chủ.

[FunctionalBlock("FB_MS_Out", ModelDomain.MasterData)]
public sealed class OutMasterBlock;

[FunctionalBlock("FB_OP_Out", ModelDomain.OperationalData)]
public sealed class OutOperationBlock;

/// <summary>App tin báo ra ngoài của team. App độc lập.</summary>
[App(Name)]
public sealed class OutApp : AppDefinition
{
    public const string Name = "OutApp";

    protected override void Define(PomBuilder pom) => pom
        .Entity<MessageBlock>().Entity<OutboundQueueItem>().Entity<ProductionReportLog>().Entity<ScrapMessageLine>().Entity<OutboundMark>()
        .Command<SetMessageBlock>().Command<InstallOutboundSchedules>().Command<SendProductionReport>().Command<SendConsumptionReport>()
        .Command<SendScrapFile>().Command<SendScrapReorder>().Command<SendQueuedMessages>();
}

public sealed class OutResponse : CommandResponse
{
    public string? NId { get; set; }

    /// <summary><c>Sent</c>, <c>Queued</c>, <c>Skipped</c> hoặc <c>Empty</c>.</summary>
    public string? Status { get; set; }

    public int Count { get; set; }

    internal static OutResponse Fail(int code, string message)
    {
        var response = new OutResponse();
        response.SetError(code, message);
        return response;
    }

    internal static OutResponse From(CommandResponse failed) => Fail(failed.Error!.ErrorCode, failed.Error.ErrorMessage);
}

public static class OutClasses
{
    public const string Production = "Production";
    public const string Consumption = "Consumption";
    public const string Scrap = "Scrap";
}

public static class OutAdapters
{
    public const string ErpProduction = "ErpProduction";
    public const string ErpConsumption = "ErpConsumption";
    public const string ScrapFile = "ScrapFile";
    public const string Reorder = "ReorderSvc";
}

public static class OutStatus
{
    public const string Sent = "Sent";
    public const string Queued = "Queued";
    public const string Skipped = "Skipped";
    public const string Empty = "Empty";
    public const string Error = "ERROR";
}

/// <summary>Material class có ý nghĩa với tin ra ngoài.</summary>
public static class OutMaterialClasses
{
    /// <summary>Linh kiện nhà cung cấp trong nước: chỉ hàng này được đặt bù khi bị hủy.</summary>
    public const string ComponentDomestic = "Component-Domestic";

    /// <summary>Bán thành phẩm chỉ có trong MOM: mã giữ nguyên trong file scrap.</summary>
    public const string InHouse = "InHouse";
}

/// <summary>Lịch chặn của một loại tin. NId = loại tin. Loại tin không có dòng nào thì luôn gửi được.</summary>
[Entity<OutMasterBlock>]
public sealed class MessageBlock : Entity
{
    /// <summary>Chặn từ ngày 1 tới ngày này của tháng; 0 là không chặn đầu tháng.</summary>
    public int FirstDaysToSkip { get; set; }

    /// <summary>Lần thứ mấy của thứ trong tháng (1–5); 0 là không chặn; 9 là lần cuối của tháng.</summary>
    public int WeekInstance { get; set; }

    /// <summary>Tên thứ tiếng Anh, vd <c>Sunday</c>.</summary>
    public string? DayOfWeek { get; set; }
}

/// <summary>Tin chờ hết chặn. NId = <c>loại|WO</c> hoặc <c>Scrap|kiểu|ngày</c>.</summary>
[Entity<OutOperationBlock>]
public sealed class OutboundQueueItem : Entity
{
    public string? MessageClass { get; set; }

    public string? WorkOrderNId { get; set; }

    public bool Monthly { get; set; }

    public int MonthlyTriggerDay { get; set; }

    public bool Sent { get; set; }

    public DateTimeOffset? SentOn { get; set; }
}

/// <summary>Lần báo sản lượng gần nhất của một WO. NId = WO.</summary>
[Entity<OutOperationBlock>]
public sealed class ProductionReportLog : Entity
{
    public decimal Quantity { get; set; }

    public DateTimeOffset? SentOn { get; set; }
}

/// <summary>Một dòng của file scrap. NId = <c>Daily|NC|MTU</c> hoặc <c>Monthly|NC|MTU</c>. Dòng ERROR được xét lại ở lần sau.</summary>
[Entity<OutOperationBlock>]
public sealed class ScrapMessageLine : Entity
{
    public string? NonConformanceNId { get; set; }

    public string? MtuNId { get; set; }

    public string? Status { get; set; }

    public string? Reason { get; set; }
}

/// <summary>Lần gửi gần nhất của một loại tin theo mốc thời gian. NId = tên loại.</summary>
[Entity<OutOperationBlock>]
public sealed class OutboundMark : Entity
{
    public DateTimeOffset LastSentOn { get; set; }
}

public static class OutboundCodes
{
    /// <summary>Mã part cha trong file scrap: hàng chỉ có trong MOM giữ nguyên; part khác đổi tiền tố, mã 12 ký tự chỉ lấy 10 ký tự đầu.</summary>
    public static string Parent(string part, string? materialClass)
    {
        ArgumentNullException.ThrowIfNull(part);
        return materialClass == OutMaterialClasses.InHouse ? part : "PA" + (part.Length == 12 ? part[..10] : part);
    }
}

internal static class Outbound
{
    /// <summary>Số trong file ra ngoài: không có số 0 thừa của cột thập phân.</summary>
    public static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private const int LastOfMonth = 9;

    public static bool IsOkToSend(IReadOnlyPlatform platform, string messageClass)
    {
        var exception = platform.ProjectionQuery<MessageBlock>().SingleOrDefault(e => e.NId == messageClass);
        if (exception is null)
        {
            return true;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = TimeZoneInfo.ConvertTime(platform.Time.GetUtcNow(), zone);
        return now.Day > exception.FirstDaysToSkip && !OnSkippedWeekday(now, exception);
    }

    private static bool OnSkippedWeekday(DateTimeOffset day, MessageBlock exception)
    {
        if (exception.WeekInstance == 0 || day.DayOfWeek.ToString() != exception.DayOfWeek)
        {
            return false;
        }

        return exception.WeekInstance == LastOfMonth
            ? day.AddDays(7).Month != day.Month
            : ((day.Day - 1) / 7) + 1 == exception.WeekInstance;
    }

    /// <summary>Đưa tin vào hàng đợi; đã có tin chưa gửi cùng khóa thì thôi.</summary>
    public static OutResponse Queue(IPlatform platform, string nid, string messageClass, string? workOrder, bool monthly = false, int triggerDay = 0)
    {
        platform.Tracer.Write(TraceLevel.Warning, $"There is a schedule exception for {messageClass} today: {nid} is queued.");
        var item = platform.Query<OutboundQueueItem>().SingleOrDefault(q => q.NId == nid);
        if (item is null)
        {
            item = platform.Create<OutboundQueueItem>();
            item.NId = nid;
        }

        item.MessageClass = messageClass;
        item.WorkOrderNId = workOrder;
        item.Monthly = monthly;
        item.MonthlyTriggerDay = triggerDay;
        item.Sent = false;
        item.SentOn = null;
        platform.Submit(item);
        return new OutResponse { NId = nid, Status = OutStatus.Queued };
    }

    public static (decimal Quantity, string? Uom) ToErp(IReadOnlyPlatform platform, string? material, decimal quantity, string? uom)
    {
        var conversion = platform.ProjectionQuery<UomConversion>().FirstOrDefault(u => u.MaterialNId == material && u.System == "Erp");
        return conversion is null ? (quantity, uom) : (quantity * conversion.Factor, conversion.DestinationUom);
    }

    public static DateTimeOffset Mark(IPlatform platform, string name) =>
        platform.Query<OutboundMark>().Where(m => m.NId == name).Select(m => (DateTimeOffset?)m.LastSentOn).SingleOrDefault() ?? DateTimeOffset.MinValue;

    public static void SetMark(IPlatform platform, string name)
    {
        var mark = platform.Query<OutboundMark>().SingleOrDefault(m => m.NId == name);
        if (mark is null)
        {
            mark = platform.Create<OutboundMark>();
            mark.NId = name;
        }

        mark.LastSentOn = platform.Time.GetUtcNow();
        platform.Submit(mark);
    }

    /// <summary>Hàng hủy: dòng NC SCRAP mà MTU đã Scrapped.</summary>
    public static List<(NonConformance Nc, Mtu Mtu)> Scrapped(IPlatform platform)
    {
        var ncs = platform.Query<NonConformance>().Where(n => n.Status == NonConformanceStatus.Scrap).ToList();
        var names = ncs.Select(n => n.NId).ToList();
        var items = platform.Query<NonConformanceItem>().Where(i => names.Contains(i.NonConformanceNId!)).ToList();
        var mtus = items.Select(i => i.MtuNId).ToList();
        var scrapped = platform.Query<Mtu>().Where(m => mtus.Contains(m.NId) && m.Status == MtuStatus.Scrapped).ToDictionary(m => m.NId);
        return [.. items.Where(i => scrapped.ContainsKey(i.MtuNId!)).Select(i => (ncs.Single(n => n.NId == i.NonConformanceNId), scrapped[i.MtuNId!]))];
    }
}

[Command<OutMasterBlock>]
public sealed class SetMessageBlock : ICommand<OutResponse>
{
    [Mandatory]
    public string? MessageClass { get; init; }

    public int FirstDaysToSkip { get; init; }

    public int WeekInstance { get; init; }

    public string? DayOfWeek { get; init; }
}

internal sealed class SetMessageBlockHandler : ICommandHandler<SetMessageBlock, OutResponse>
{
    public OutResponse Handle(SetMessageBlock command, IPlatform platform)
    {
        var row = platform.Query<MessageBlock>().SingleOrDefault(e => e.NId == command.MessageClass) ?? platform.Create<MessageBlock>();
        row.NId = command.MessageClass!;
        row.FirstDaysToSkip = command.FirstDaysToSkip;
        row.WeekInstance = command.WeekInstance;
        row.DayOfWeek = command.DayOfWeek;
        platform.Submit(row);
        return new OutResponse { NId = row.NId };
    }
}

/// <summary>Timer và signal rule của tin ra ngoài: hàng đợi mỗi giờ, file scrap ngày, file scrap tháng, đặt bù mỗi ngày.</summary>
[Command<OutOperationBlock>]
public sealed class InstallOutboundSchedules : ICommand<OutResponse>
{
    public int MonthlyTriggerDay { get; init; } = 5;
}

internal sealed class InstallOutboundSchedulesHandler : ICommandHandler<InstallOutboundSchedules, OutResponse>
{
    private const int Hour = 3_600;
    private const int Day = 86_400;

    public OutResponse Handle(InstallOutboundSchedules command, IPlatform platform)
    {
        var now = platform.Time.GetUtcNow();
        var nextHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        var nextDay = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 5, 0, TimeSpan.Zero).AddDays(1);
        var plans = new (string NId, DateTimeOffset Due, int Interval, string Command, object Parameters)[]
        {
            ("OUT_QUEUE", nextHour, Hour, nameof(SendQueuedMessages), new { }),
            ("OUT_SCRAP_DAILY", nextDay, Day, nameof(SendScrapFile), new { Monthly = false }),
            ("OUT_SCRAP_MONTHLY", nextDay, Day, nameof(SendScrapFile), new { Monthly = true, command.MonthlyTriggerDay }),
            ("OUT_REORDER", nextDay, Day, nameof(SendScrapReorder), new { }),
        };
        foreach (var plan in plans)
        {
            var timer = platform.CallCommand<CreateTimer, FoundationResponse>(new CreateTimer { NId = plan.NId, FirstDueOn = plan.Due, IntervalSeconds = plan.Interval });
            if (!timer.Succeeded)
            {
                return OutResponse.From(timer);
            }

            var rule = platform.CallCommand<DefineSignalRule, FoundationResponse>(new DefineSignalRule
            {
                NId = plan.NId,
                Trigger = "Timer",
                TimerNId = plan.NId,
                App = OutApp.Name,
                Command = plan.Command,
                Parameters = JsonSerializer.Serialize(plan.Parameters),
            });
            if (!rule.Succeeded)
            {
                return OutResponse.From(rule);
            }
        }

        return new OutResponse { Count = plans.Length };
    }
}

/// <summary>Báo khi WO Complete: sản lượng, rồi tiêu hao. Chạy ở worker riêng, sau khi WO đã commit; lỗi ở đây không làm WO đổi lại.</summary>
[EventSubscriber<OutOperationBlock>(Subscription = "WorkOrderReports", Predefined = true, WorkerRole = WorkerRoleName)]
public sealed class WorkOrderReporter : IEventSubscriber<WorkOrderStatusChanged>
{
    public const string WorkerRoleName = "Outbound";

    public void Handle(WorkOrderStatusChanged domainEvent, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(platform);
        if (domainEvent.ToStatus != WorkOrderStates.Complete)
        {
            return;
        }

        var sent = platform.CallCommand<SendProductionReport, OutResponse>(new SendProductionReport { WorkOrderNId = domainEvent.NId });
        if (!sent.Succeeded)
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Production report of {domainEvent.NId} failed: {sent.Error!.ErrorMessage}");
        }
    }
}

/// <summary>
/// Báo sản lượng của WO cho ERP, rồi gọi báo tiêu hao. Không báo khi WO có material dùng theo nhu cầu; đã báo cùng số lượng thì thôi; đã qua
/// tháng của ngày kết thúc thì không báo lại. Ngày bị chặn thì vào hàng đợi.
/// </summary>
[Command<OutOperationBlock>]
public sealed class SendProductionReport : ICommand<OutResponse>
{
    [Mandatory]
    public string? WorkOrderNId { get; init; }
}

internal sealed class SendProductionReportHandler : ICommandHandler<SendProductionReport, OutResponse>
{
    public OutResponse Handle(SendProductionReport command, IPlatform platform)
    {
        var workOrder = platform.Query<WorkOrder>().SingleOrDefault(w => w.NId == command.WorkOrderNId);
        if (workOrder is null)
        {
            return OutResponse.Fail(ProductErrors.UnknownWorkOrder, $"Work order {command.WorkOrderNId} does not exist.");
        }

        var operations = platform.Query<WorkOrderOperation>().Where(o => o.WorkOrderNId == workOrder.NId).Select(o => o.NId).ToList();
        if (platform.Query<WorkOrderOperationMaterial>().Any(m => operations.Contains(m.WorkOrderOperationNId!) && m.Usage == "AsRequired"))
        {
            return new OutResponse { NId = workOrder.NId, Status = OutStatus.Skipped };
        }

        var log = platform.Query<ProductionReportLog>().SingleOrDefault(l => l.NId == workOrder.NId);
        var now = platform.Time.GetUtcNow();
        if (log is not null && (log.Quantity == workOrder.ProducedQuantity || log.SentOn?.Month != now.Month))
        {
            return new OutResponse { NId = workOrder.NId, Status = OutStatus.Skipped };
        }

        if (!Outbound.IsOkToSend(platform, OutClasses.Production))
        {
            return Outbound.Queue(platform, OutClasses.Production + "|" + workOrder.NId, OutClasses.Production, workOrder.NId);
        }

        var material = platform.ProjectionQuery<Material>().SingleOrDefault(m => m.NId == workOrder.ProductMaterialNId);
        var (quantity, uom) = Outbound.ToErp(platform, workOrder.ProductMaterialNId, workOrder.ProducedQuantity, material?.UnitOfMeasure);
        platform.SendMessageAsync(new OutboundMessage(OutAdapters.ErpProduction, "ProductionReport", JsonSerializer.Serialize(new
        {
            recordType = log is null ? "I" : "U",
            plant = "NV01",
            workOrder = workOrder.NId,
            material = workOrder.ProductMaterialNId,
            quantity,
            uom,
            date = now,
        })));

        if (log is null)
        {
            log = platform.Create<ProductionReportLog>();
            log.NId = workOrder.NId;
        }

        log.Quantity = workOrder.ProducedQuantity;
        log.SentOn = now;
        platform.Submit(log);

        var consumption = platform.CallCommand<SendConsumptionReport, OutResponse>(new SendConsumptionReport { WorkOrderNId = workOrder.NId });
        return consumption.Succeeded ? new OutResponse { NId = workOrder.NId, Status = OutStatus.Sent } : consumption;
    }
}

/// <summary>Báo tiêu hao của WO: cộng theo material, đổi đơn vị ERP. Lần trừ cho sản phẩm đã hủy không tính; material dùng theo nhu cầu không báo.</summary>
[Command<OutOperationBlock>]
public sealed class SendConsumptionReport : ICommand<OutResponse>
{
    [Mandatory]
    public string? WorkOrderNId { get; init; }
}

internal sealed class SendConsumptionReportHandler : ICommandHandler<SendConsumptionReport, OutResponse>
{
    public OutResponse Handle(SendConsumptionReport command, IPlatform platform)
    {
        if (!Outbound.IsOkToSend(platform, OutClasses.Consumption))
        {
            return Outbound.Queue(platform, OutClasses.Consumption + "|" + command.WorkOrderNId, OutClasses.Consumption, command.WorkOrderNId);
        }

        var rows = platform.Query<MaterialConsumption>().Where(c => c.WorkOrderNId == command.WorkOrderNId).ToList();
        var products = rows.Select(r => r.ProductMtuNId).Distinct().ToList();
        var components = rows.Select(r => r.ComponentMtuNId).Distinct().ToList();
        var scrapped = platform.Query<Mtu>().Where(m => products.Contains(m.NId) && m.Status == MtuStatus.Scrapped).Select(m => m.NId).ToHashSet();
        var materials = platform.Query<Mtu>().Where(m => components.Contains(m.NId)).ToDictionary(m => m.NId, m => (m.MaterialNId, m.UnitOfMeasure));
        var operations = platform.Query<WorkOrderOperation>().Where(o => o.WorkOrderNId == command.WorkOrderNId).Select(o => o.NId).ToList();
        var asRequired = platform.Query<WorkOrderOperationMaterial>().Where(m => operations.Contains(m.WorkOrderOperationNId!) && m.Usage == "AsRequired").Select(m => m.MaterialNId).ToHashSet();
        var lines = rows.Where(r => !scrapped.Contains(r.ProductMtuNId!))
            .GroupBy(r => materials[r.ComponentMtuNId!])
            .Where(g => !asRequired.Contains(g.Key.MaterialNId))
            .Select(g =>
            {
                var (quantity, uom) = Outbound.ToErp(platform, g.Key.MaterialNId, g.Sum(r => r.Quantity), g.Key.UnitOfMeasure);
                return new { material = g.Key.MaterialNId, quantity, uom };
            }).ToList();
        if (lines.Count == 0)
        {
            return new OutResponse { NId = command.WorkOrderNId, Status = OutStatus.Empty };
        }

        platform.SendMessageAsync(new OutboundMessage(OutAdapters.ErpConsumption, "ConsumptionReport",
            JsonSerializer.Serialize(new { plant = "NV01", workOrder = command.WorkOrderNId, lines })));
        return new OutResponse { NId = command.WorkOrderNId, Status = OutStatus.Sent, Count = lines.Count };
    }
}

/// <summary>
/// File scrap. Hằng ngày: mọi dòng chưa gửi. Hằng tháng: chỉ chạy đúng ngày cấu hình, chỉ hai mã hủy do nhà cung cấp và lẫn công đoạn (<c>S1</c>, <c>S2</c>). Dòng thiếu scrap tag
/// hoặc không tra được nhà cung cấp bị đánh ERROR và loại khỏi file.
/// </summary>
[Command<OutOperationBlock>]
public sealed class SendScrapFile : ICommand<OutResponse>
{
    public bool Monthly { get; init; }

    public int MonthlyTriggerDay { get; init; }
}

internal sealed class SendScrapFileHandler : ICommandHandler<SendScrapFile, OutResponse>
{
    public OutResponse Handle(SendScrapFile command, IPlatform platform)
    {
        var now = platform.Time.GetUtcNow();
        if (command.Monthly && now.Day != command.MonthlyTriggerDay)
        {
            return new OutResponse { Status = OutStatus.Skipped };
        }

        var kind = command.Monthly ? "Monthly" : "Daily";
        if (!Outbound.IsOkToSend(platform, OutClasses.Scrap))
        {
            return Outbound.Queue(platform, $"{OutClasses.Scrap}|{kind}|{now:yyyyMMdd}", OutClasses.Scrap, null, command.Monthly, command.MonthlyTriggerDay);
        }

        var csv = new StringBuilder("Part,Parent,Supplier,CostCenter,Quantity,ScrapCode,Model,ScrapTag\n");
        var sent = 0;
        foreach (var (nc, mtu) in Outbound.Scrapped(platform))
        {
            var nid = kind + "|" + nc.NId + "|" + mtu.NId;
            var line = platform.Query<ScrapMessageLine>().SingleOrDefault(l => l.NId == nid);
            if (line?.Status == OutStatus.Sent)
            {
                continue;
            }

            var detail = platform.Query<NonConformanceDetail>().SingleOrDefault(d => d.EntityId == nc.Id);
            if (command.Monthly && detail?.ScrapCode is not ("S1" or "S2"))
            {
                continue;
            }

            var supplier = platform.ProjectionQuery<MaterialSupplier>().Where(s => s.MaterialNId == mtu.MaterialNId).Select(s => s.SupplierNId).FirstOrDefault()
                ?? detail?.SupplierNId;
            var reason = string.IsNullOrEmpty(detail?.ScrapTag) ? "No scrap tag" : supplier is null ? $"No supplier for {mtu.MaterialNId}" : null;
            if (line is null)
            {
                line = platform.Create<ScrapMessageLine>();
                line.NId = nid;
                line.NonConformanceNId = nc.NId;
                line.MtuNId = mtu.NId;
            }

            line.Status = reason is null ? OutStatus.Sent : OutStatus.Error;
            line.Reason = reason;
            platform.Submit(line);
            if (reason is not null)
            {
                platform.Tracer.Write(TraceLevel.Warning, $"Scrap line {nid} left out: {reason}.");
                continue;
            }

            csv.Append(CultureInfo.InvariantCulture, $"{mtu.MaterialNId},{Parent(platform, mtu)},{supplier},{detail!.ChargeToCostCenter},{Outbound.Number(mtu.Quantity)},{detail.ScrapCode},{detail.ModelCode},{detail.ScrapTag}\n");
            sent++;
        }

        if (sent == 0)
        {
            return new OutResponse { Status = OutStatus.Empty };
        }

        platform.SendMessageAsync(new OutboundMessage(OutAdapters.ScrapFile, "Scrap" + kind, csv.ToString(), "text/csv"));
        return new OutResponse { Status = OutStatus.Sent, Count = sent };
    }

    private static string Parent(IPlatform platform, Mtu mtu)
    {
        var product = platform.Query<MaterialConsumption>().Where(c => c.ComponentMtuNId == mtu.NId).Select(c => c.ProductMtuNId).FirstOrDefault();
        var part = product is null ? mtu.MaterialNId : platform.Query<Mtu>().Where(m => m.NId == product).Select(m => m.MaterialNId).Single();
        var materialClass = platform.ProjectionQuery<Material>().Where(m => m.NId == part).Select(m => m.MaterialClass).SingleOrDefault();
        return OutboundCodes.Parent(part!, materialClass);
    }
}

/// <summary>Đặt bù hàng scrap: lần theo genealogy từ hàng hủy từ lần gửi trước, chỉ lấy linh kiện thuộc class trong nước; cộng theo nhà cung cấp, dock, part.</summary>
[Command<OutOperationBlock>]
public sealed class SendScrapReorder : ICommand<OutResponse>;

internal sealed class SendScrapReorderHandler : ICommandHandler<SendScrapReorder, OutResponse>
{
    private const string MarkName = "ScrapReorder";

    public OutResponse Handle(SendScrapReorder command, IPlatform platform)
    {
        var since = Outbound.Mark(platform, MarkName);
        var parts = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (_, mtu) in Outbound.Scrapped(platform).Where(s => s.Nc.LastUpdatedOn > since))
        {
            Collect(platform, mtu.NId, mtu.MaterialNId!, mtu.Quantity, parts, []);
        }

        Outbound.SetMark(platform, MarkName);
        if (parts.Count == 0)
        {
            return new OutResponse { Status = OutStatus.Empty };
        }

        var body = new StringBuilder();
        foreach (var (part, quantity) in parts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var supplier = platform.ProjectionQuery<MaterialSupplier>().Where(s => s.MaterialNId == part).Select(s => s.SupplierNId).FirstOrDefault();
            var dock = platform.ProjectionQuery<Kanban>().Where(k => k.MaterialNId == part).Select(k => k.DockCode).FirstOrDefault();
            body.Append(CultureInfo.InvariantCulture, $"{supplier},{dock},{part},{Outbound.Number(quantity)}\n");
        }

        platform.SendMessageAsync(new OutboundMessage(OutAdapters.Reorder, "ScrapReorder", body.ToString(), "text/plain"));
        return new OutResponse { Status = OutStatus.Sent, Count = parts.Count };
    }

    private static void Collect(IPlatform platform, string mtu, string material, decimal quantity, Dictionary<string, decimal> parts, HashSet<string> seen)
    {
        if (!seen.Add(mtu))
        {
            return;
        }

        var materialClass = platform.ProjectionQuery<Material>().Where(m => m.NId == material).Select(m => m.MaterialClass).SingleOrDefault();
        if (materialClass == OutMaterialClasses.ComponentDomestic)
        {
            parts[material] = parts.GetValueOrDefault(material) + quantity;
        }

        foreach (var used in platform.Query<MaterialConsumption>().Where(c => c.ProductMtuNId == mtu).ToList())
        {
            var part = platform.Query<Mtu>().Where(m => m.NId == used.ComponentMtuNId).Select(m => m.MaterialNId).SingleOrDefault();
            if (part is not null)
            {
                Collect(platform, used.ComponentMtuNId!, part, used.Quantity, parts, seen);
            }
        }
    }
}

/// <summary>Timer mỗi giờ: gửi tối đa 20 tin của các loại đang được phép, cũ nhất trước. Tin không gửi được vẫn nằm trong hàng đợi.</summary>
[Command<OutOperationBlock>]
public sealed class SendQueuedMessages : ICommand<OutResponse>
{
    public const int BatchSize = 20;
}

internal sealed class SendQueuedMessagesHandler : ICommandHandler<SendQueuedMessages, OutResponse>
{
    public OutResponse Handle(SendQueuedMessages command, IPlatform platform)
    {
        var allowed = new[] { OutClasses.Production, OutClasses.Consumption, OutClasses.Scrap }.Where(c => Outbound.IsOkToSend(platform, c)).ToList();
        var items = platform.Query<OutboundQueueItem>().Where(q => !q.Sent && allowed.Contains(q.MessageClass!))
            .OrderBy(q => q.CreatedOn).ThenBy(q => q.NId).Take(SendQueuedMessages.BatchSize).ToList();
        var sent = 0;
        foreach (var item in items)
        {
            CommandResponse response = item.MessageClass switch
            {
                OutClasses.Production => platform.CallCommand<SendProductionReport, OutResponse>(new SendProductionReport { WorkOrderNId = item.WorkOrderNId }),
                OutClasses.Consumption => platform.CallCommand<SendConsumptionReport, OutResponse>(new SendConsumptionReport { WorkOrderNId = item.WorkOrderNId }),
                _ => platform.CallCommand<SendScrapFile, OutResponse>(new SendScrapFile { Monthly = item.Monthly, MonthlyTriggerDay = platform.Time.GetUtcNow().Day }),
            };
            if (!response.Succeeded)
            {
                platform.Tracer.Write(TraceLevel.Warning, $"Queued message {item.NId} stays queued: {response.Error!.ErrorMessage}");
                continue;
            }

            item.Sent = true;
            item.SentOn = platform.Time.GetUtcNow();
            platform.Submit(item);
            sent++;
        }

        return new OutResponse { Status = sent > 0 ? OutStatus.Sent : OutStatus.Empty, Count = sent };
    }
}
