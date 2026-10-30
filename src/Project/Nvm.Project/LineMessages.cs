using System.Globalization;
using System.Text.Json;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Tin từ xưởng: máy ghi bảng ở LineDB, trigger chép sang bảng trung gian (schema relay), một trong hai đường chuyển tin
// (relay nghe notify, hoặc signal rule gọi IngestLineMessages) phát LineMessageArrived. Handler của team tách tin, gọi
// ApplyLineResults, rồi ghi kết quả từng dòng về LineDB kèm mã lỗi ghép.

/// <summary>Trạng thái của một dòng tin ở bảng trung gian LineDB.</summary>
public static class LineRowStatus
{
    /// <summary>Dòng mới, chờ gửi.</summary>
    public const string New = "NEW";

    /// <summary>Đã gửi sang MOM, chờ kết quả.</summary>
    public const string Sent = "SENT";

    /// <summary>MOM đã ghi.</summary>
    public const string Done = "DONE";

    /// <summary>Notify lỗi phía LineDB (vd payload vượt giới hạn). Chỉ có ở đường relay; không tự gửi lại.</summary>
    public const string NotifyError = "NOTIFY_ERR";

    /// <summary>MOM chưa ghi được, kèm mã lỗi ghép; gửi lại hay không tùy chính sách theo cặp (trạng thái, mã lỗi).</summary>
    public const string Retry = "RETRY";

    /// <summary>Dừng: hết lượt gửi lại, hoặc lỗi không gửi lại.</summary>
    public const string Failed = "FAILED";
}

/// <summary>Loại xử lý của handler: chữ số đầu của mã lỗi ghép.</summary>
public enum LineProcessType
{
    NonConformance = 4,
    Shrink = 5,
    Apply = 6,
}

/// <summary>Mã lỗi gốc của việc xử lý tin; mã ghi về LineDB là mã ghép <see cref="Composite"/>.</summary>
public static class LineErrors
{
    public const int Malformed = 101;
    public const int UnknownOperationCode = 110;
    public const int UnknownEquipmentCode = 120;
    public const int UnknownMaterialCode = 130;
    public const int OperationNotFound = 200;
    public const int OutputFailed = 300;
    public const int ConsumptionFailed = 310;
    public const int ProgressFailed = 320;
    public const int UnknownMtu = 400;
    public const int SplitFailed = 410;
    public const int NonConformanceFailed = 420;
    public const int PackingFailed = 500;
    public const int NotInstalled = 900;
    public const int Unexpected = 999;

    /// <summary>Mã ghi về LineDB: chữ số của loại xử lý nối với mã gốc, rồi đổi dấu. Lỗi 200 khi ghi operation thành -6200.</summary>
    public static int Composite(LineProcessType type, int code) =>
        -int.Parse(((int)type).ToString(CultureInfo.InvariantCulture) + Math.Abs(code).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
}

/// <summary>Một lần một serial đi qua một bước, đã ghi vào MOM. NId = <c>&lt;serial&gt;|&lt;mã bước phía xưởng&gt;</c>.</summary>
/// <remarks>Khóa tự nhiên này làm đường tin máy idempotent (K7): tin gửi lại không tạo MTU, không trừ nguyên liệu lần nữa.</remarks>
[Entity<NvOperationBlock>]
public sealed class LineExecution : Entity
{
    [Mandatory]
    public string? Serial { get; set; }

    public string? OperationCode { get; set; }

    public string? EquipmentCode { get; set; }

    public string? WorkOrderNId { get; set; }

    public string? WorkOrderOperationNId { get; set; }

    public string? ProductMtuNId { get; set; }

    public decimal Quantity { get; set; }

    public DateTimeOffset? StartTime { get; set; }

    public DateTimeOffset? EndTime { get; set; }

    /// <summary><c>OK</c> hoặc <c>KO</c>.</summary>
    public string? StatusId { get; set; }

    public string? StatusCode { get; set; }

    /// <summary>Dấu chạy thử trong tin (<c>TM</c>, <c>FM</c>), rỗng với hàng thường.</summary>
    public string? RunMode { get; set; }

    /// <summary>Dòng ở bảng trung gian LineDB mang tin này.</summary>
    public long? SequenceId { get; set; }
}

/// <summary>Kết quả của một sản phẩm ở một bước, đúng các trường của một tin.</summary>
public sealed class LineOperationDetail
{
    public long? SequenceId { get; init; }

    public string? OperationCode { get; init; }

    public string? EquipmentCode { get; init; }

    public string? Serial { get; init; }

    /// <summary>Rỗng: 1.</summary>
    public decimal? Quantity { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    public DateTimeOffset? EndTime { get; init; }

    /// <summary><c>OK</c> hoặc <c>KO</c>; mã của OK là <c>00</c>.</summary>
    public string? StatusId { get; init; }

    public string? StatusCode { get; init; }

    /// <summary>Chế độ máy: <c>TM</c> (trial), <c>FM</c> (flow), rỗng với hàng thường.</summary>
    public string? RunMode { get; init; }

    public IReadOnlyList<LineMaterialUse>? Materials { get; init; }

    /// <summary>Mã vật chứa nhận sản phẩm ở bước đóng tote.</summary>
    public string? ContainerCode { get; init; }

    /// <summary>Mã các vật chứa nằm trong vật chứa của tin (tin xếp pallet: các tote).</summary>
    public IReadOnlyList<string>? Contents { get; init; }
}

/// <summary>Nguyên liệu máy báo đã dùng. Không có lô: trừ từ MTU tạm <c>Auto_…</c>. Không có lượng: theo định mức.</summary>
public sealed class LineMaterialUse
{
    public string? MaterialCode { get; init; }

    public string? Lot { get; init; }

    public decimal? Quantity { get; init; }
}

public sealed record LineError(int Code, string Message);

public sealed record NotExecutedOperation(long? SequenceId, string? Serial, LineError Error);

/// <summary>
/// <see cref="CommandResponse.Succeeded"/> chỉ nói lời gọi chạy xong. Serial nào đã ghi, serial nào chưa (kèm lý do) nằm ở
/// hai danh sách (lab L09).
/// </summary>
public sealed class ApplyLineResultsResponse : CommandResponse
{
    public IList<string> ExecutedSerialNumbers { get; } = [];

    public IList<NotExecutedOperation> NotExecutedOperations { get; } = [];

    /// <summary>Serial có tin KO.</summary>
    public IList<string> PendingNonConformance { get; } = [];

    /// <summary>
    /// Mọi serial đã ghi (kể cả tin gửi lại), đủ thông tin cho bước xử lý hàng lỗi <see cref="HandleLineNonConformances"/>:
    /// tin OK cũng có thể tách hao hụt, mang dấu chạy thử, hay đóng NC ở bước kết luận.
    /// </summary>
    public IList<LineNonConformanceData> NonConformanceData { get; } = [];
}

/// <summary>
/// Ghi kết quả máy vào MOM theo các trường của tin: dịch mã phía xưởng, chọn bước WO, tạo MTU sản phẩm và gắn vào WO, trừ
/// nguyên liệu, ghi bước. Mỗi serial một kết quả; serial không ghi được không làm hỏng các serial khác.
/// </summary>
[Command<NvOperationBlock>]
public sealed class ApplyLineResults : ICommand<ApplyLineResultsResponse>
{
    [Mandatory]
    public IReadOnlyList<LineOperationDetail>? Operations { get; init; }
}

internal sealed class ApplyLineResultsHandler : ICommandHandler<ApplyLineResults, ApplyLineResultsResponse>
{
    private static readonly string[] Open = [WorkOrderStates.Active, WorkOrderStates.New, WorkOrderStates.Pause];

    public ApplyLineResultsResponse Handle(ApplyLineResults command, IPlatform platform)
    {
        var response = new ApplyLineResultsResponse();
        var codes = LineCodeSet.Load(platform);
        var packing = platform.ProjectionQuery<PackingOperation>().ToDictionary(p => p.NId, StringComparer.Ordinal);
        foreach (var detail in command.Operations!)
        {
            var error = Apply(platform, codes, packing.GetValueOrDefault(detail.OperationCode ?? string.Empty), detail, response);
            if (error is null)
            {
                response.ExecutedSerialNumbers.Add(detail.Serial!);
                continue;
            }

            platform.Tracer.Write(TraceLevel.Error, $"Serial {detail.Serial} ({detail.OperationCode}) not executed: {error.Code} {error.Message}");
            response.NotExecutedOperations.Add(new NotExecutedOperation(detail.SequenceId, detail.Serial, error));
        }

        return response;
    }

    private static LineError? Apply(IPlatform platform, LineCodeSet codes, PackingOperation? packing, LineOperationDetail detail,
        ApplyLineResultsResponse response)
    {
        if (string.IsNullOrEmpty(detail.Serial))
        {
            return new LineError(LineErrors.Malformed, "Message has no serial number.");
        }

        var operationNId = codes.Operation(detail.OperationCode);
        if (operationNId is null && packing is null)
        {
            return new LineError(LineErrors.UnknownOperationCode, $"Line operation code {detail.OperationCode} has no mapping.");
        }

        if (codes.Equipment(detail.EquipmentCode) is not { } equipmentNId)
        {
            return new LineError(LineErrors.UnknownEquipmentCode, $"Line equipment code {detail.EquipmentCode} has no mapping.");
        }

        var executionNId = detail.Serial + "|" + detail.OperationCode;
        if (platform.Query<LineExecution>().SingleOrDefault(e => e.NId == executionNId) is { } recorded)
        {
            platform.Tracer.Write(TraceLevel.Information, $"Serial {detail.Serial} at {detail.OperationCode} is already recorded; nothing to do.");
            if (operationNId is not null)
            {
                response.NonConformanceData.Add(LineNonConformanceData.Of(detail, recorded.ProductMtuNId!, operationNId, equipmentNId));
            }

            return null;
        }

        if (packing?.Action == PackingActions.Tote && string.IsNullOrEmpty(detail.ContainerCode))
        {
            return new LineError(LineErrors.PackingFailed, $"Message for {detail.Serial} at {detail.OperationCode} has no tote code.");
        }

        if (operationNId is null)
        {
            // Bước chỉ đóng gói (vd xếp pallet): không ghi WO, chỉ vật chứa và dòng chống ghi hai lần.
            var packed = packing!.Action == PackingActions.Pallet ? null : LinePacking.ProductBySerial(platform, detail.Serial);
            if (LinePacking.Apply(platform, packing, detail, packed, equipmentNId) is { } packingError)
            {
                return packingError;
            }

            Record(platform, executionNId, detail, workOrder: null, step: null, packed, detail.Quantity ?? 1m);
            return null;
        }

        var step = FindStep(platform, detail.Serial, operationNId, equipmentNId);
        if (step is null && OpenSpare(platform, operationNId, equipmentNId))
        {
            step = FindStep(platform, detail.Serial, operationNId, equipmentNId);
        }

        if (step is null)
        {
            return new LineError(LineErrors.OperationNotFound,
                $"No work order operation found for serial {detail.Serial} (operation {operationNId}, equipment {equipmentNId}).");
        }

        var workOrder = platform.Query<WorkOrder>().Single(w => w.NId == step.WorkOrderNId);
        var quantity = detail.Quantity ?? 1m;

        // Dịch và tính hết nguyên liệu trước khi ghi: lỗi sau khi đã ghi để lại dữ liệu dở trong transaction chung.
        var mapped = new List<(string Material, LineMaterialUse Use)>();
        foreach (var use in detail.Materials ?? [])
        {
            if (codes.Material(use.MaterialCode) is not { } material)
            {
                return new LineError(LineErrors.UnknownMaterialCode, $"Line material code '{use.MaterialCode}' has no mapping.");
            }

            mapped.Add((material, use));
        }

        var planned = platform.Query<WorkOrderOperationMaterial>().Where(m => m.WorkOrderOperationNId == step.NId).ToList();
        var consumptions = new List<(string Material, string? Lot, decimal Quantity)>();
        foreach (var norm in planned)
        {
            var reported = mapped.Where(m => m.Material == norm.MaterialNId).Select(m => m.Use).ToList();
            if (reported.Count == 0)
            {
                consumptions.Add((norm.MaterialNId!, null, norm.Quantity * quantity));
                continue;
            }

            consumptions.AddRange(reported.Select(r => (norm.MaterialNId!, r.Lot, r.Quantity ?? norm.Quantity * quantity / reported.Count)));
        }

        foreach (var extra in mapped.Where(m => planned.All(p => p.MaterialNId != m.Material)))
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Serial {detail.Serial}: material {extra.Material} is not planned for {step.NId}; ignored.");
        }

        var isLast = !platform.Query<WorkOrderOperation>().Any(o => o.WorkOrderNId == workOrder.NId && o.Sequence > step.Sequence);
        var productNId = workOrder.ProductMaterialNId + "_" + detail.Serial;
        var exists = platform.Query<Mtu>().Any(m => m.NId == productNId);

        // Tin của bước sau tới trước (đường poll đọc theo từng mã bước): MTU đã được tạo như lô nguyên liệu với lượng đủ trừ, chưa
        // thuộc WO nào, lượng đã bị nâng tạm mỗi lần trừ. Tin làm ra nó tới thì lượng = lượng của tin trừ phần đã trừ, không thì
        // lô giữ lượng tạm (vd cuộn 0 m thay vì 120 m).
        // MTU có serial giữ lượng 1, nên chỉ lô (Batch) cần đặt lại.
        var placeholder = exists && platform.Query<Mtu>().Any(m => m.NId == productNId && m.Kind == "Batch")
            && !platform.Query<WorkOrderOutput>().Any(o => o.MtuNId == productNId);
        var output = platform.CallCommand<RecordWorkOrderOutput, ProductResponse>(new RecordWorkOrderOutput
        {
            WorkOrderNId = workOrder.NId,
            MtuNId = productNId,
            CreateMtu = !exists,
            Quantity = exists ? null : quantity,
            Code = detail.Serial,
            EquipmentNId = equipmentNId,
            QuantityIncrement = isLast ? quantity : null,
        });
        if (!output.Succeeded)
        {
            return new LineError(LineErrors.OutputFailed, output.Error!.ErrorMessage);
        }

        if (placeholder)
        {
            var consumed = platform.Query<MaterialConsumption>().Where(c => c.ComponentMtuNId == productNId).Sum(c => (decimal?)c.Quantity) ?? 0m;
            var left = Math.Max(quantity - consumed, 0m);
            platform.Tracer.Write(TraceLevel.Warning,
                $"{productNId} was created by a later step before this message; produced {quantity}, {consumed} already consumed, {left} left.");
            var raised = platform.CallCommand<EditMtu, ProductResponse>(new EditMtu { NId = productNId, Quantity = left });
            if (!raised.Succeeded)
            {
                return new LineError(LineErrors.OutputFailed, raised.Error!.ErrorMessage);
            }
        }

        foreach (var (material, lot, amount) in consumptions)
        {
            var component = Component(platform, material, lot, amount, detail, equipmentNId);
            if (component.Error is { } failed)
            {
                return new LineError(LineErrors.ConsumptionFailed, failed);
            }

            var consumed = platform.CallCommand<RecordConsumption, ProductResponse>(new RecordConsumption
            {
                WorkOrderNId = workOrder.NId,
                OperationNId = operationNId,
                ProductMtuNId = productNId,
                ComponentMtuNId = component.NId,
                Quantity = amount,
            });
            if (!consumed.Succeeded)
            {
                return new LineError(LineErrors.ConsumptionFailed, consumed.Error!.ErrorMessage);
            }
        }

        var progress = platform.CallCommand<RecordOperationProgress, ProductResponse>(
            new RecordOperationProgress { WorkOrderOperationNId = step.NId, Status = "Active", EquipmentNId = equipmentNId });
        if (!progress.Succeeded)
        {
            return new LineError(LineErrors.ProgressFailed, progress.Error!.ErrorMessage);
        }

        if (packing is not null && LinePacking.Apply(platform, packing, detail, productNId, equipmentNId) is { } packError)
        {
            return packError;
        }

        Record(platform, executionNId, detail, workOrder.NId, step.NId, productNId, quantity);
        if (string.Equals(detail.StatusId, "KO", StringComparison.Ordinal))
        {
            response.PendingNonConformance.Add(detail.Serial);
        }

        response.NonConformanceData.Add(LineNonConformanceData.Of(detail, productNId, operationNId, equipmentNId));
        return null;
    }

    private static void Record(IPlatform platform, string executionNId, LineOperationDetail detail, string? workOrder, string? step,
        string? productNId, decimal quantity)
    {
        var execution = platform.Create<LineExecution>();
        execution.NId = executionNId;
        execution.Serial = detail.Serial;
        execution.OperationCode = detail.OperationCode;
        execution.EquipmentCode = detail.EquipmentCode;
        execution.WorkOrderNId = workOrder;
        execution.WorkOrderOperationNId = step;
        execution.ProductMtuNId = productNId;
        execution.Quantity = quantity;
        execution.StartTime = detail.StartTime;
        execution.EndTime = detail.EndTime;
        execution.StatusId = detail.StatusId;
        execution.StatusCode = detail.StatusCode;
        execution.RunMode = detail.RunMode;
        execution.SequenceId = detail.SequenceId;
        platform.Submit(execution);
    }

    /// <summary>
    /// Bước WO cho serial: (1) serial đã thuộc một WO thì lấy bước của WO đó khớp bước và máy; (2) không thì bước khớp bước và
    /// máy trong WO đang Active, New hoặc Pause, WO bắt đầu muộn nhất trước.
    /// </summary>
    private static WorkOrderOperation? FindStep(IPlatform platform, string serial, string operationNId, string equipmentNId)
    {
        var onMachine = platform.Query<WorkOrderOperationEquipment>().Where(e => e.EquipmentNId == equipmentNId)
            .Select(e => e.WorkOrderOperationNId!).ToList();
        var candidates = platform.Query<WorkOrderOperation>().Where(o => onMachine.Contains(o.NId) && o.OperationNId == operationNId).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var owners = (from mtu in platform.Query<Mtu>()
                      where mtu.Code == serial
                      join output in platform.Query<WorkOrderOutput>() on mtu.NId equals output.MtuNId
                      select output.WorkOrderNId).ToList();
        if (candidates.FirstOrDefault(c => owners.Contains(c.WorkOrderNId)) is { } owned)
        {
            return owned;
        }

        var workOrders = candidates.Select(c => c.WorkOrderNId!).ToList();
        var open = platform.Query<WorkOrder>().Where(w => workOrders.Contains(w.NId) && Open.Contains(w.Status!))
            .OrderByDescending(w => w.EstimatedStart).Select(w => w.NId).FirstOrDefault();
        return open is null ? null : candidates.First(c => c.WorkOrderNId == open);
    }

    /// <summary>WO dự phòng cho mọi process có bước này trên máy này, nếu giờ hiện tại nằm trong khung ca của process đó.</summary>
    private static bool OpenSpare(IPlatform platform, string operationNId, string equipmentNId)
    {
        var allowed = platform.ProjectionQuery<OperationEquipment>().Where(e => e.EquipmentNId == equipmentNId)
            .Select(e => e.ProcessOperationNId!).ToList();
        var processNIds = platform.ProjectionQuery<ProcessOperation>().Where(o => allowed.Contains(o.NId) && o.OperationNId == operationNId)
            .Select(o => o.ProcessNId!).Distinct().ToList();
        var opened = false;
        foreach (var process in platform.ProjectionQuery<Process>().Where(p => processNIds.Contains(p.NId)).ToList())
        {
            platform.Tracer.Write(TraceLevel.Information, $"No open work order for {process.NId} on {equipmentNId}; trying a spare one.");
            opened |= ShiftWorkOrders.OpenSpare(platform, process, platform.Time.GetUtcNow());
        }

        return opened;
    }

    /// <summary>MTU nguyên liệu để trừ: lô trong tin (chưa có thì tạo, thiếu lượng thì nâng), hoặc MTU tạm <c>Auto_…</c>.</summary>
    private static (string NId, string? Error) Component(IPlatform platform, string material, string? lot, decimal amount,
        LineOperationDetail detail, string equipmentNId)
    {
        if (string.IsNullOrEmpty(lot))
        {
            var temp = $"Auto_{material}_{detail.Serial}_{detail.OperationCode}";
            var created = platform.CallCommand<CreateMtu, ProductResponse>(
                new CreateMtu { NId = temp, MaterialNId = material, Quantity = amount, EquipmentNId = equipmentNId });
            return (temp, created.Succeeded ? null : created.Error!.ErrorMessage);
        }

        // Lô con tách ra mang cùng mã với lô mẹ; phần hao hụt tách ra đã bị hủy thì không được trừ tiếp vào đó.
        var existing = platform.Query<Mtu>().Where(m => m.Code == lot && m.MaterialNId == material && m.Status != MtuStatus.Scrapped)
            .OrderByDescending(m => m.Quantity).FirstOrDefault();
        if (existing is null)
        {
            var nid = material + "_" + lot;
            var created = platform.CallCommand<CreateMtu, ProductResponse>(
                new CreateMtu { NId = nid, MaterialNId = material, Quantity = amount, Code = lot, EquipmentNId = equipmentNId });
            return (nid, created.Succeeded ? null : created.Error!.ErrorMessage);
        }

        if (existing.Quantity < amount)
        {
            platform.Tracer.Write(TraceLevel.Warning,
                $"Lot {existing.NId} has {existing.Quantity}, {amount} reported for {detail.Serial}; raising the lot quantity.");
            var raised = platform.CallCommand<EditMtu, ProductResponse>(new EditMtu { NId = existing.NId, Quantity = amount });
            if (!raised.Succeeded)
            {
                return (existing.NId, raised.Error!.ErrorMessage);
            }
        }

        return (existing.NId, null);
    }
}

/// <summary>Bốn bảng đối chiếu, đọc một lần cho cả lời gọi (domain master data, nên qua <c>ProjectionQuery</c>).</summary>
internal sealed class LineCodeSet
{
    private readonly Dictionary<string, string> _operations;
    private readonly Dictionary<string, string> _equipment;
    private readonly Dictionary<string, string> _materials;

    private LineCodeSet(Dictionary<string, string> operations, Dictionary<string, string> equipment, Dictionary<string, string> materials)
    {
        _operations = operations;
        _equipment = equipment;
        _materials = materials;
    }

    public static LineCodeSet Load(IReadOnlyPlatform platform) => new(
        platform.ProjectionQuery<LineOperationCode>().ToDictionary(c => c.NId, c => c.OperationNId!, StringComparer.Ordinal),
        platform.ProjectionQuery<LineEquipmentCode>().ToDictionary(c => c.NId, c => c.EquipmentNId!, StringComparer.Ordinal),
        platform.ProjectionQuery<LineMaterialCode>().ToDictionary(c => c.NId, c => c.MaterialNId!, StringComparer.Ordinal));

    public string? Operation(string? code) => code is not null && _operations.TryGetValue(code, out var value) ? value : null;

    public string? Equipment(string? code) => code is not null && _equipment.TryGetValue(code, out var value) ? value : null;

    public string? Material(string? code) => code is not null && _materials.TryGetValue(code, out var value) ? value : null;
}

/// <summary>Có tin mới từ LineDB. <see cref="Payload"/> là JSON <c>{"rows":[…]}</c> đúng như LineDB gửi.</summary>
[Event<NvOperationBlock>]
[EventVersion(1)]
public sealed record LineMessageArrived(string Source, string Payload) : IFoundationEvent;

/// <summary>
/// Handler của team cho tin từ xưởng. Từ đây hai đường chuyển tin như nhau: tách tin theo dạng, tin hao hụt đi riêng, còn lại
/// gọi <see cref="ApplyLineResults"/>; rồi ghi kết quả từng dòng về LineDB. Lỗi ở đây không về người gọi nào: chỉ thấy trong
/// trace và trong trạng thái dòng tin.
/// </summary>
/// <remarks>Kết quả ghi về LineDB trước khi transaction của MOM commit; commit hỏng thì dòng đã báo DONE mà MOM không có dữ liệu.</remarks>
[EventSubscriber<NvOperationBlock>(Subscription = "LineMessages", Predefined = true, WorkerRole = WorkerRoleName)]
public sealed class LineMessageHandler : IEventSubscriber<LineMessageArrived>
{
    public const string WorkerRoleName = "LineMessages";

    public void Handle(LineMessageArrived domainEvent, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(platform);
        var rows = LineMessageParser.Parse(domainEvent.Payload);
        var results = new List<LineRowResult>();
        foreach (var row in rows.Where(r => r.Detail is null && r.Shrink is null))
        {
            platform.Tracer.Write(TraceLevel.Error, $"Line row {row.SequenceId} ({row.Kind}) not processed: {row.Error}");
            results.Add(new LineRowResult(row.SequenceId, LineRowStatus.Failed, LineErrors.Composite(LineProcessType.Apply, LineErrors.Malformed)));
        }

        var shrinks = rows.Where(r => r.Shrink is not null).ToList();
        if (shrinks.Count > 0)
        {
            var response = platform.CallCommand<HandleLineShrink, LineQualityResponse>(new HandleLineShrink { Shrinks = [.. shrinks.Select(s => s.Shrink!)] });
            results.AddRange(shrinks.Select(s => Result(s.SequenceId, LineProcessType.Shrink, response)));
        }

        var units = rows.Where(r => r.Detail is not null).ToList();
        if (units.Count > 0)
        {
            var response = platform.CallCommand<ApplyLineResults, ApplyLineResultsResponse>(
                new ApplyLineResults { Operations = [.. units.Select(u => u.Detail!)] });
            var notExecuted = response.NotExecutedOperations.Where(n => n.SequenceId is not null).ToDictionary(n => n.SequenceId!.Value);
            LineQualityResponse? quality = null;
            if (response.Succeeded && response.NonConformanceData.Count > 0)
            {
                quality = platform.CallCommand<HandleLineNonConformances, LineQualityResponse>(
                    new HandleLineNonConformances { Items = [.. response.NonConformanceData] });
            }

            foreach (var unit in units)
            {
                results.Add(!response.Succeeded
                    ? new LineRowResult(unit.SequenceId, LineRowStatus.Retry, LineErrors.Composite(LineProcessType.Apply, LineErrors.Unexpected))
                    : notExecuted.TryGetValue(unit.SequenceId, out var failed)
                        ? new LineRowResult(unit.SequenceId, LineRowStatus.Retry, LineErrors.Composite(LineProcessType.Apply, failed.Error.Code))
                        : quality is null
                            ? new LineRowResult(unit.SequenceId, LineRowStatus.Done, 0)
                            : Result(unit.SequenceId, LineProcessType.NonConformance, quality));
            }
        }

        if (platform.Endpoint(NvEndpoints.LineDb) is not { } lineDb)
        {
            platform.Tracer.Write(TraceLevel.Error, $"Endpoint {NvEndpoints.LineDb} is not configured; {results.Count} row(s) stay {LineRowStatus.Sent}.");
            return;
        }

        LineDbClient.WriteResults(lineDb, results);
        platform.Tracer.Write(TraceLevel.Information,
            $"Line rows from {domainEvent.Source}: {results.Count(r => r.Status == LineRowStatus.Done)} done, "
            + $"{results.Count(r => r.Status != LineRowStatus.Done)} not done.");
    }

    private static LineRowResult Result(long sequenceId, LineProcessType type, LineQualityResponse response) =>
        !response.Succeeded
            ? new LineRowResult(sequenceId, LineRowStatus.Retry, LineErrors.Composite(type, LineErrors.Unexpected))
            : response.NotHandled.FirstOrDefault(n => n.SequenceId == sequenceId) is { } failed
                ? new LineRowResult(sequenceId, LineRowStatus.Retry, LineErrors.Composite(type, failed.Error.Code))
                : new LineRowResult(sequenceId, LineRowStatus.Done, 0);
}

/// <summary>Kết quả của một dòng tin, ghi về LineDB.</summary>
public sealed record LineRowResult(long SequenceId, string Status, int ErrorCode);

/// <summary>
/// Lệnh đọc tin của đường poll: signal rule mỗi phút gọi một lần cho mỗi mã bước. Đánh dấu các dòng đã lấy là SENT ở LineDB,
/// rồi phát cùng event mà relay phát. Đường đang dùng là <see cref="NvKeys.LineTransfer"/>; khác <c>poll</c> thì không làm gì.
/// </summary>
/// <remarks>Dòng đã đánh dấu SENT nằm ngoài transaction của MOM: commit hỏng thì dòng chờ chính sách gửi lại.</remarks>
[Command<NvOperationBlock>]
public sealed class IngestLineMessages : ICommand<NvResponse>
{
    [Mandatory]
    public string? OperationCode { get; init; }
}

internal sealed class IngestLineMessagesHandler : ICommandHandler<IngestLineMessages, NvResponse>
{
    public NvResponse Handle(IngestLineMessages command, IPlatform platform)
    {
        var keys = platform.ProjectionQuery<ConfigurationKey>()
            .Where(k => k.NId == NvKeys.LineTransfer || k.NId == NvKeys.LinePollBatchSize).ToDictionary(k => k.NId, k => k.Val);
        if (!string.Equals(keys.GetValueOrDefault(NvKeys.LineTransfer), "poll", StringComparison.Ordinal))
        {
            platform.Tracer.Write(TraceLevel.Verbose, $"{NvKeys.LineTransfer} is not 'poll'; nothing read.");
            return new NvResponse { NId = command.OperationCode };
        }

        if (platform.Endpoint(NvEndpoints.LineDb) is not { } lineDb)
        {
            return NvResponse.Fail(NvErrors.MissingEndpoint, $"Endpoint {NvEndpoints.LineDb} is not configured.");
        }

        var batch = int.TryParse(keys.GetValueOrDefault(NvKeys.LinePollBatchSize), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : 50;
        var (payload, rows) = LineDbClient.Claim(lineDb, command.OperationCode!, batch, platform.Time.GetUtcNow());
        if (rows > 0)
        {
            platform.Tracer.Write(TraceLevel.Information, $"Claimed {rows} line row(s) for {command.OperationCode}.");
            platform.RaiseEventOnCommit(new LineMessageArrived("poll", payload));
        }

        return new NvResponse { NId = command.OperationCode };
    }
}

/// <summary>Bật đường poll cho một mã bước: timer <c>LINE_POLL_&lt;mã&gt;</c> và signal rule gọi <see cref="IngestLineMessages"/>.</summary>
[Command<NvMasterBlock>]
public sealed class SetLinePolling : ICommand<NvResponse>
{
    [Mandatory]
    public string? OperationCode { get; init; }

    /// <summary>Rỗng hoặc 0: 60 giây.</summary>
    public int IntervalSeconds { get; init; }
}

internal sealed class SetLinePollingHandler : ICommandHandler<SetLinePolling, NvResponse>
{
    public NvResponse Handle(SetLinePolling command, IPlatform platform)
    {
        var nid = "LINE_POLL_" + command.OperationCode;
        var interval = command.IntervalSeconds > 0 ? command.IntervalSeconds : 60;
        var timer = platform.CallCommand<CreateTimer, FoundationResponse>(
            new CreateTimer { NId = nid, FirstDueOn = platform.Time.GetUtcNow().AddSeconds(interval), IntervalSeconds = interval });
        if (!timer.Succeeded)
        {
            return NvResponse.Fail(timer.Error!.ErrorCode, timer.Error.ErrorMessage);
        }

        var rule = platform.CallCommand<DefineSignalRule, FoundationResponse>(new DefineSignalRule
        {
            NId = nid,
            Trigger = "Timer",
            TimerNId = nid,
            App = ProductionApp.Name,
            Command = NvProductionExtApp.Name + "_" + nameof(IngestLineMessages),
            Parameters = JsonSerializer.Serialize(new { command.OperationCode }),
        });
        return rule.Succeeded ? new NvResponse { NId = nid } : NvResponse.Fail(rule.Error!.ErrorCode, rule.Error.ErrorMessage);
    }
}
