using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Hàng lỗi từ tin máy: sau khi ghi kết quả (ApplyLineResults), handler của team xử lý NC cho từng serial đã ghi, và tách
// hao hụt cho tin hao hụt. Hai lệnh này là loại xử lý 4 (NC) và 5 (hao hụt) trong mã lỗi ghép về LineDB.

/// <summary>Một serial đã ghi, kèm những gì bước xử lý NC cần (không đọc lại tin).</summary>
public sealed class LineNonConformanceData
{
    public long? SequenceId { get; init; }

    public string? Serial { get; init; }

    public string? ProductMtuNId { get; init; }

    public string? OperationCode { get; init; }

    public string? OperationNId { get; init; }

    public string? EquipmentNId { get; init; }

    public string? StatusId { get; init; }

    public string? StatusCode { get; init; }

    /// <summary>Giờ máy xong, trong tin. Mọi phép so trước sau của NC dùng giờ này, không dùng thứ tự tin tới.</summary>
    public DateTimeOffset? EventTime { get; init; }

    public string? RunMode { get; init; }

    internal static LineNonConformanceData Of(LineOperationDetail detail, string productMtuNId, string operationNId, string equipmentNId) => new()
    {
        SequenceId = detail.SequenceId,
        Serial = detail.Serial,
        ProductMtuNId = productMtuNId,
        OperationCode = detail.OperationCode,
        OperationNId = operationNId,
        EquipmentNId = equipmentNId,
        StatusId = detail.StatusId,
        StatusCode = detail.StatusCode,
        EventTime = detail.EndTime,
        RunMode = detail.RunMode,
    };
}

/// <summary>Tin hao hụt: máy báo một lượng của lô nguyên liệu bị mất ở một bước.</summary>
public sealed class LineShrinkDetail
{
    public long? SequenceId { get; init; }

    public string? OperationCode { get; init; }

    public string? EquipmentCode { get; init; }

    public string? MaterialCode { get; init; }

    public string? Lot { get; init; }

    public decimal? Quantity { get; init; }

    public string? UnitOfMeasure { get; init; }

    public string? StatusCode { get; init; }

    public DateTimeOffset? EventTime { get; init; }
}

/// <summary>Như <see cref="ApplyLineResultsResponse"/>: <c>Succeeded</c> chỉ nói lời gọi chạy xong; mục chưa xử lý nằm ở danh sách.</summary>
public sealed class LineQualityResponse : CommandResponse
{
    public IList<string> NonConformances { get; } = [];

    public IList<NotExecutedOperation> NotHandled { get; } = [];
}

/// <summary>Một tin hao hụt đã xử lý. NId = <c>&lt;lô&gt;|&lt;mã bước&gt;|&lt;giờ trong tin&gt;</c>, chặn tách hai lần khi gửi lại.</summary>
[Entity<NvOperationBlock>]
public sealed class LineShrinkExecution : Entity
{
    public string? LotMtuNId { get; set; }

    public string? ChildMtuNId { get; set; }

    public string? NonConformanceNId { get; set; }

    public long? SequenceId { get; set; }
}

/// <summary>
/// Xử lý NC cho các serial đã ghi: đưa mã tin OK về <c>00</c>; gắn và truyền dấu chạy thử; đóng NC đang chờ tin OK ở bước
/// này; chọn cấu hình hủy; tách hao hụt nếu cấu hình có lượng tách; tạo NC với trạng thái ban đầu theo cấu hình (không cấu
/// hình: NC chung OPEN). Mỗi serial một kết quả.
/// </summary>
[Command<NvOperationBlock>]
public sealed class HandleLineNonConformances : ICommand<LineQualityResponse>
{
    [Mandatory]
    public IReadOnlyList<LineNonConformanceData>? Items { get; init; }
}

internal sealed class HandleLineNonConformancesHandler : ICommandHandler<HandleLineNonConformances, LineQualityResponse>
{
    public LineQualityResponse Handle(HandleLineNonConformances command, IPlatform platform)
    {
        var response = new LineQualityResponse();
        foreach (var item in command.Items!)
        {
            var error = HandleOne(platform, item, response);
            if (error is not null)
            {
                platform.Tracer.Write(TraceLevel.Error, $"Serial {item.Serial} ({item.OperationCode}) non-conformance not handled: {error.Code} {error.Message}");
                response.NotHandled.Add(new NotExecutedOperation(item.SequenceId, item.Serial, error));
            }
        }

        return response;
    }

    private static LineError? HandleOne(IPlatform platform, LineNonConformanceData item, LineQualityResponse response)
    {
        var mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == item.ProductMtuNId);
        if (mtu is null || item.OperationCode is null)
        {
            return new LineError(LineErrors.UnknownMtu, $"MTU {item.ProductMtuNId} does not exist.");
        }

        var ok = string.Equals(item.StatusId, "OK", StringComparison.Ordinal);
        var code = ok ? "00" : item.StatusCode ?? string.Empty;
        var time = item.EventTime ?? platform.Time.GetUtcNow();

        var mode = item.RunMode ?? RunModeMarks.Inherited(platform, mtu.NId);
        if (mode is not null)
        {
            RunModeMarks.Mark(platform, mtu.NId, mode, response);
        }

        if (ok)
        {
            LineNonConformances.CloseWaiting(platform, item.Serial, item.OperationCode, time);
        }

        var config = ScrapConfigs.Select(platform, item.OperationCode, code, mode is not null, item.EquipmentNId, time);
        if (config is null)
        {
            if (ok || LineNonConformances.Exists(platform, mtu.NId, item.OperationCode, code))
            {
                return null;
            }

            var defect = platform.ProjectionQuery<LineFailureCode>().Where(f => f.NId == code).Select(f => f.DefectNId).SingleOrDefault()
                ?? LineNonConformances.DefaultDefect;
            var generic = LineNonConformances.Create(platform, mtu, NonConformanceStatus.Open, defect, item.OperationNId, d =>
            {
                d.Origin = NonConformanceOrigins.Line;
                d.Serial = item.Serial;
                d.SourceMtuNId = mtu.NId;
                d.OperationCode = item.OperationCode;
                d.StatusCode = code;
                d.EventTime = time;
            });
            return Collect(generic, response);
        }

        // Dòng chạy thử: hàng mang dấu bị xử như hỏng, mã của NC là dấu. Tin OK của hàng thường chỉ đi tiếp khi cấu hình tách.
        var ncCode = mode ?? (ok ? null : code);
        if (mode is null && ok && config.SplitQuantity is null)
        {
            return null;
        }

        if (LineNonConformances.Exists(platform, mtu.NId, item.OperationCode, ncCode))
        {
            return null;
        }

        var target = mtu;
        if (config.SplitQuantity is { } split)
        {
            if (LineNonConformances.Split(platform, mtu, split, config.SplitUnitOfMeasure) is not { } child)
            {
                return new LineError(LineErrors.SplitFailed, $"Failed to split MTU {mtu.NId} for non-conformance processing.");
            }

            target = child;
        }

        var created = LineNonConformances.CreateFromConfig(platform, config, target, mtu, item.Serial, item.OperationCode, item.OperationNId, ncCode, time);
        return Collect(created, response);
    }

    private static LineError? Collect((NonConformance? Nc, string? Error) created, LineQualityResponse response)
    {
        if (created.Nc is null)
        {
            return new LineError(LineErrors.NonConformanceFailed, created.Error!);
        }

        response.NonConformances.Add(created.Nc.NId);
        return null;
    }
}

/// <summary>Tạo và kết luận NC theo luật của team; dùng chung cho tin máy, tin hao hụt và dấu chạy thử.</summary>
internal static class LineNonConformances
{
    /// <summary>Defect khi mã lỗi máy chưa có trong bảng đối chiếu (<c>OTHER</c>).</summary>
    public const string DefaultDefect = "Z";

    public static bool Exists(IReadOnlyPlatform platform, string sourceMtuNId, string operationCode, string? statusCode) =>
        platform.Query<NonConformanceDetail>().Any(d => d.SourceMtuNId == sourceMtuNId && d.OperationCode == operationCode
            && d.StatusCode == statusCode && (d.Origin == NonConformanceOrigins.Line || d.Origin == NonConformanceOrigins.Shrink));

    /// <summary>Tạo NC qua lệnh của lớp Product rồi ghi facet của team. Trả lý do khi lớp Product từ chối.</summary>
    public static (NonConformance? Nc, string? Error) Create(IPlatform platform, Mtu mtu, string status, string? defect, string? operationNId,
        Action<NonConformanceDetail> describe, string? nid = null, string? severity = null)
    {
        var created = platform.CallCommand<CreateNonConformance, ProductResponse>(new CreateNonConformance
        {
            NId = nid ?? QualityData.NewNcNId(platform, mtu.NId),
            MtuNIds = [mtu.NId],
            Status = status,
            Type = "QUALITY",
            Severity = severity,
            DefectNId = defect,
            OperationNId = operationNId,
        });
        if (!created.Succeeded)
        {
            return (null, created.Error!.ErrorMessage);
        }

        var nc = platform.Query<NonConformance>().Single(n => n.NId == created.NId);
        var detail = QualityData.DetailOrNew(platform, nc);
        detail.SupplierNId = QualityData.Supplier(platform, mtu.MaterialNId);
        describe(detail);
        platform.Submit(detail);
        return (nc, null);
    }

    /// <summary>
    /// NC theo một dòng cấu hình. Có bước kết luận: NC để mở, kết luận chờ tin OK ở bước đó (tin đó đã tới trước, với giờ muộn
    /// hơn, thì kết luận ngay). Không có: trạng thái ban đầu là kết luận tự động. SCRAP tự động cần material có đúng một nhà cung
    /// cấp, không thì NC để mở.
    /// </summary>
    public static (NonConformance? Nc, string? Error) CreateFromConfig(IPlatform platform, OperationScrapConfig config, Mtu target, Mtu source,
        string? serial, string operationCode, string? operationNId, string? statusCode, DateTimeOffset time, string origin = NonConformanceOrigins.Line)
    {
        var waits = config.DispositionOperationCode is not null;
        var initial = waits ? NonConformanceStatus.Open : config.AutoDispositionStatus ?? NonConformanceStatus.Open;
        if (initial == NonConformanceStatus.Scrap && QualityData.Supplier(platform, target.MaterialNId) is null)
        {
            platform.Tracer.Write(TraceLevel.Warning,
                $"Material {target.MaterialNId} has no single supplier; automatic scrap of {target.NId} skipped, non-conformance stays {NonConformanceStatus.Open}.");
            initial = NonConformanceStatus.Open;
        }

        var created = Create(platform, target, initial == NonConformanceStatus.Scrap ? initial : NonConformanceStatus.Open, null, operationNId, d =>
        {
            d.Origin = origin;
            d.Serial = serial;
            d.SourceMtuNId = source.NId;
            d.OperationCode = operationCode;
            d.StatusCode = statusCode;
            d.EventTime = time;
            d.DispositionOperationCode = config.DispositionOperationCode;
            d.PendingDisposition = waits ? config.AutoDispositionStatus : null;
            d.ScrapCode = config.ScrapCode;
            d.ProblemCode = config.ProblemCode;
            d.ModelCode = config.ModelCode;
            d.CreatingCostCenter = config.CreatingCostCenter;
            d.ChargeToCostCenter = config.ChargeToCostCenter;
        });
        if (created.Nc is not { } nc)
        {
            return created;
        }

        if (initial == NonConformanceStatus.UseAsIs)
        {
            Dispose(platform, nc, NonConformanceStatus.UseAsIs);
        }
        else if (waits && config.AutoDispositionStatus is { } pending && config.DispositionOperationCode != operationCode && serial is not null
            && platform.Query<LineExecution>().Any(e => e.Serial == serial && e.OperationCode == config.DispositionOperationCode && e.StatusId == "OK" && e.EndTime > time))
        {
            platform.Tracer.Write(TraceLevel.Information, $"OK at {config.DispositionOperationCode} for {serial} is later than {time:O}; {nc.NId} is {pending} at once.");
            Dispose(platform, nc, pending);
        }

        return created;
    }

    /// <summary>Tin OK ở bước kết luận: kết luận mọi NC từ tin máy đang chờ bước này mà giờ trong tin của NC sớm hơn.</summary>
    public static void CloseWaiting(IPlatform platform, string? serial, string operationCode, DateTimeOffset time)
    {
        var waiting = platform.Query<NonConformanceDetail>()
            .Where(d => d.Serial == serial && d.DispositionOperationCode == operationCode && d.PendingDisposition != null && d.EventTime < time
                && (d.Origin == NonConformanceOrigins.Line || d.Origin == NonConformanceOrigins.Shrink))
            .ToList();
        foreach (var detail in waiting)
        {
            var nc = platform.Query<NonConformance>().Single(n => n.Id == detail.EntityId);
            if (nc.Status == NonConformanceStatus.Open)
            {
                platform.Tracer.Write(TraceLevel.Information, $"OK at {operationCode} for {serial} closes {nc.NId} as {detail.PendingDisposition}.");
                Dispose(platform, nc, detail.PendingDisposition!);
            }
        }
    }

    public static void Dispose(IPlatform platform, NonConformance nc, string status)
    {
        var result = platform.CallCommand<SetNonConformanceStatus, ProductResponse>(new SetNonConformanceStatus { NId = nc.NId, Status = status });
        if (!result.Succeeded)
        {
            platform.Tracer.Write(TraceLevel.Error, $"Disposition of {nc.NId} to {status} failed: {result.Error!.ErrorMessage}");
        }
    }

    /// <summary>
    /// Tách lượng hao hụt ra MTU con. Đơn vị so phân biệt hoa thường như code của team: <c>m</c> khác <c>M</c>. Lỗi chỉ ghi vào
    /// trace; người gọi chỉ nhận câu chung, không có lý do (lab L12).
    /// </summary>
    public static Mtu? Split(IPlatform platform, Mtu mtu, decimal quantity, string? unit)
    {
        if (!string.Equals(unit, mtu.UnitOfMeasure, StringComparison.Ordinal))
        {
            platform.Tracer.Write(TraceLevel.Error, $"Split of {mtu.NId}: unit '{unit}' of the scrap configuration is not the MTU unit '{mtu.UnitOfMeasure}'.");
            return null;
        }

        if (quantity > mtu.Quantity)
        {
            platform.Tracer.Write(TraceLevel.Error, $"Split of {mtu.NId}: {quantity} is more than the MTU quantity {mtu.Quantity}.");
            return null;
        }

        var split = platform.CallCommand<SplitMtu, ProductResponse>(new SplitMtu { NId = mtu.NId, Quantity = quantity });
        if (!split.Succeeded)
        {
            platform.Tracer.Write(TraceLevel.Error, $"Split of {mtu.NId}: {split.Error!.ErrorMessage}");
            return null;
        }

        return platform.Query<Mtu>().Single(m => m.NId == split.NId);
    }
}

/// <summary>Dấu chạy thử: gắn từ tin, nhận từ thành phần, truyền xuôi genealogy tới sản phẩm đã làm (tin tới không theo thứ tự).</summary>
internal static class RunModeMarks
{
    public static string? Of(IReadOnlyPlatform platform, Guid mtuId) =>
        platform.Query<MtuRunMode>().Where(m => m.EntityId == mtuId).Select(m => m.Mode).SingleOrDefault();

    /// <summary>Dấu của thành phần trực tiếp, nếu có.</summary>
    public static string? Inherited(IReadOnlyPlatform platform, string mtuNId)
    {
        var components = platform.Query<MaterialConsumption>().Where(c => c.ProductMtuNId == mtuNId).Select(c => c.ComponentMtuNId).ToList();
        var ids = platform.Query<Mtu>().Where(m => components.Contains(m.NId)).Select(m => m.Id).ToList();
        return platform.Query<MtuRunMode>().Where(m => ids.Contains(m.EntityId)).Select(m => m.Mode).FirstOrDefault();
    }

    /// <summary>
    /// Gắn dấu cho MTU và mọi sản phẩm làm từ nó. Sản phẩm vừa nhận dấu mà bước đã ghi của nó có dòng chạy thử thì tạo NC
    /// ngay ở bước đó.
    /// </summary>
    public static void Mark(IPlatform platform, string mtuNId, string mode, LineQualityResponse response)
    {
        Set(platform, mtuNId, mode);
        foreach (var product in QualityData.Downstream(platform, mtuNId))
        {
            if (!Set(platform, product, mode))
            {
                continue;
            }

            var target = platform.Query<Mtu>().Single(m => m.NId == product);
            foreach (var execution in platform.Query<LineExecution>().Where(e => e.ProductMtuNId == product).ToList())
            {
                var equipment = platform.ProjectionQuery<LineEquipmentCode>().Where(c => c.NId == execution.EquipmentCode).Select(c => c.EquipmentNId).SingleOrDefault();
                var time = execution.EndTime ?? platform.Time.GetUtcNow();
                if (ScrapConfigs.Select(platform, execution.OperationCode!, "00", true, equipment, time) is { } config
                    && !LineNonConformances.Exists(platform, product, execution.OperationCode!, mode))
                {
                    platform.Tracer.Write(TraceLevel.Information, $"{product} got mark {mode} after {execution.OperationCode} was recorded; trial configuration applies now.");
                    var created = LineNonConformances.CreateFromConfig(platform, config, target, target, execution.Serial, execution.OperationCode!, null, mode, time);
                    if (created.Nc is { } nc)
                    {
                        response.NonConformances.Add(nc.NId);
                    }
                }
            }
        }
    }

    /// <summary>Gắn dấu; trả false nếu MTU đã có dấu.</summary>
    private static bool Set(IPlatform platform, string mtuNId, string mode)
    {
        var mtu = platform.Query<Mtu>().Single(m => m.NId == mtuNId);
        if (Of(platform, mtu.Id) is not null)
        {
            return false;
        }

        var mark = platform.Create<MtuRunMode>();
        mark.EntityId = mtu.Id;
        mark.Mode = mode;
        platform.Submit(mark);
        return true;
    }
}

/// <summary>
/// Tin hao hụt: tìm bước WO đang mở của máy; lô chưa có thì tạo, thiếu thì nâng; tách lượng hao hụt ra lô con; NC trên lô
/// con theo cấu hình của bước (thường là SCRAP ngay). Lô mẹ không có NC nên vẫn dùng tiếp.
/// </summary>
[Command<NvOperationBlock>]
public sealed class HandleLineShrink : ICommand<LineQualityResponse>
{
    [Mandatory]
    public IReadOnlyList<LineShrinkDetail>? Shrinks { get; init; }
}

internal sealed class HandleLineShrinkHandler : ICommandHandler<HandleLineShrink, LineQualityResponse>
{
    private static readonly string[] Open = [WorkOrderStates.Active, WorkOrderStates.New, WorkOrderStates.Pause];

    public LineQualityResponse Handle(HandleLineShrink command, IPlatform platform)
    {
        var response = new LineQualityResponse();
        var codes = LineCodeSet.Load(platform);
        foreach (var shrink in command.Shrinks!)
        {
            var error = HandleOne(platform, codes, shrink, response);
            if (error is not null)
            {
                platform.Tracer.Write(TraceLevel.Error, $"Shrink of lot {shrink.Lot} ({shrink.OperationCode}) not handled: {error.Code} {error.Message}");
                response.NotHandled.Add(new NotExecutedOperation(shrink.SequenceId, shrink.Lot, error));
            }
        }

        return response;
    }

    private static LineError? HandleOne(IPlatform platform, LineCodeSet codes, LineShrinkDetail shrink, LineQualityResponse response)
    {
        if (string.IsNullOrEmpty(shrink.Lot) || shrink.Quantity is not > 0 || shrink.OperationCode is null)
        {
            return new LineError(LineErrors.Malformed, "Shrink message needs a lot, an operation and a positive quantity.");
        }

        if (codes.Operation(shrink.OperationCode) is not { } operationNId)
        {
            return new LineError(LineErrors.UnknownOperationCode, $"Line operation code {shrink.OperationCode} has no mapping.");
        }

        if (codes.Equipment(shrink.EquipmentCode) is not { } equipmentNId)
        {
            return new LineError(LineErrors.UnknownEquipmentCode, $"Line equipment code {shrink.EquipmentCode} has no mapping.");
        }

        if (codes.Material(shrink.MaterialCode) is not { } material)
        {
            return new LineError(LineErrors.UnknownMaterialCode, $"Line material code '{shrink.MaterialCode}' has no mapping.");
        }

        var time = shrink.EventTime ?? platform.Time.GetUtcNow();
        var nid = $"{shrink.Lot}|{shrink.OperationCode}|{time.UtcDateTime:O}";
        if (platform.Query<LineShrinkExecution>().Any(e => e.NId == nid))
        {
            platform.Tracer.Write(TraceLevel.Information, $"Shrink {nid} is already recorded; nothing to do.");
            return null;
        }

        var onMachine = platform.Query<WorkOrderOperationEquipment>().Where(e => e.EquipmentNId == equipmentNId).Select(e => e.WorkOrderOperationNId!).ToList();
        var workOrders = platform.Query<WorkOrderOperation>().Where(o => onMachine.Contains(o.NId) && o.OperationNId == operationNId).Select(o => o.WorkOrderNId!).ToList();
        if (!platform.Query<WorkOrder>().Any(w => workOrders.Contains(w.NId) && Open.Contains(w.Status!)))
        {
            return new LineError(LineErrors.OperationNotFound, $"Operation not found for shrink at {equipmentNId} ({operationNId}).");
        }

        var amount = shrink.Quantity.Value;
        var lot = platform.Query<Mtu>().Where(m => m.Code == shrink.Lot && m.MaterialNId == material).OrderByDescending(m => m.Quantity).FirstOrDefault();
        if (lot is null)
        {
            var created = platform.CallCommand<CreateMtu, ProductResponse>(
                new CreateMtu { NId = material + "_" + shrink.Lot, MaterialNId = material, Quantity = amount, Code = shrink.Lot, EquipmentNId = equipmentNId });
            if (!created.Succeeded)
            {
                return new LineError(LineErrors.ConsumptionFailed, created.Error!.ErrorMessage);
            }

            lot = platform.Query<Mtu>().Single(m => m.NId == created.NId);
        }
        else if (lot.Quantity < amount)
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Lot {lot.NId} has {lot.Quantity}, shrink of {amount} reported; raising the lot quantity.");
            var raised = platform.CallCommand<EditMtu, ProductResponse>(new EditMtu { NId = lot.NId, Quantity = amount });
            if (!raised.Succeeded)
            {
                return new LineError(LineErrors.ConsumptionFailed, raised.Error!.ErrorMessage);
            }
        }

        if (LineNonConformances.Split(platform, lot, amount, lot.UnitOfMeasure) is not { } child)
        {
            return new LineError(LineErrors.SplitFailed, $"Failed to split MTU {lot.NId} for shrink.");
        }

        var config = ScrapConfigs.Select(platform, shrink.OperationCode, shrink.StatusCode ?? string.Empty, false, equipmentNId, time)
            ?? new OperationScrapConfig { OperationCode = shrink.OperationCode, StatusCode = shrink.StatusCode };
        var nc = LineNonConformances.CreateFromConfig(platform, config, child, lot, shrink.Lot, shrink.OperationCode, operationNId, shrink.StatusCode, time,
            NonConformanceOrigins.Shrink);
        if (nc.Nc is null)
        {
            return new LineError(LineErrors.NonConformanceFailed, nc.Error!);
        }

        var execution = platform.Create<LineShrinkExecution>();
        execution.NId = nid;
        execution.LotMtuNId = lot.NId;
        execution.ChildMtuNId = child.NId;
        execution.NonConformanceNId = nc.Nc.NId;
        execution.SequenceId = shrink.SequenceId;
        platform.Submit(execution);
        response.NonConformances.Add(nc.Nc.NId);
        return null;
    }
}
