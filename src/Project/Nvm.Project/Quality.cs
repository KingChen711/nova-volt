using System.Globalization;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Hàng lỗi, phần của team: danh mục chất lượng, cấu hình hủy theo bước, thông tin team gắn lên NC của lớp Product
// (facet), dấu chạy thử trên MTU, bộ đếm (scrap tag, mã NCM/PCR).

/// <summary>Loại của một mã trong danh mục chất lượng.</summary>
public static class QualityCodeKinds
{
    public const string ScrapCode = "ScrapCode";
    public const string ProblemCode = "ProblemCode";
    public const string ModelCode = "ModelCode";
    public const string CostCenter = "CostCenter";
    public const string NonConformanceReason = "NonConformanceReason";

    public static readonly IReadOnlyList<string> All = [ScrapCode, ProblemCode, ModelCode, CostCenter, NonConformanceReason];
}

/// <summary>
/// Một mã của danh mục chất lượng (scrap code, problem code, model code, cost center, lý do NC). Năm danh mục cùng một khuôn:
/// mã và tên đều không trùng trong cùng loại. NId = <c>&lt;loại&gt;_&lt;mã&gt;</c>.
/// </summary>
[Entity<NvMasterBlock>]
public sealed class QualityCode : Entity
{
    [Mandatory]
    public string? Kind { get; set; }

    [Mandatory]
    public string? Code { get; set; }

    [Mandatory]
    public string? Name { get; set; }

    internal static string NIdOf(string kind, string code) => kind + "_" + code;
}

/// <summary>Nhà cung cấp của một material. NC lấy nhà cung cấp theo material của MTU, chỉ khi material có đúng một.</summary>
[Entity<NvMasterBlock>]
public sealed class MaterialSupplier : Entity
{
    [Mandatory]
    public string? MaterialNId { get; set; }

    [Mandatory]
    public string? SupplierNId { get; set; }
}

/// <summary>
/// Cấu hình hủy theo bước: bước phía xưởng nhận mã trạng thái này trong ca này thì NC xử lý thế nào. Khóa nghiệp vụ là
/// (bước, mã trạng thái, ca, cờ chạy thử); NId do người nạp đặt.
/// </summary>
[Entity<NvMasterBlock>]
public sealed class OperationScrapConfig : Entity
{
    /// <summary>Mã bước phía xưởng (vd <c>ca010</c>), không phải bước của process: mọi process NV1 cùng một bước OP10.</summary>
    [Mandatory]
    public string? OperationCode { get; set; }

    /// <summary>Mã trạng thái máy; tin OK là <c>00</c>.</summary>
    [Mandatory]
    public string? StatusCode { get; set; }

    /// <summary>Production schedule: chọn line (khu của PS) và ca.</summary>
    [Mandatory]
    public string? ScheduleNId { get; set; }

    /// <summary>Dòng của hàng chạy thử: tin có dấu chạy thử chỉ dùng dòng có cờ này, và ngược lại.</summary>
    public bool IsFlowMode { get; set; }

    /// <summary>Kết luận tự động: <c>SCRAP</c>, <c>USE AS IS</c> hoặc rỗng (NC để mở).</summary>
    public string? AutoDispositionStatus { get; set; }

    /// <summary>Có thì NC để mở tới khi một tin OK ở bước này tới (theo giờ trong tin), rồi mới kết luận.</summary>
    public string? DispositionOperationCode { get; set; }

    /// <summary>Lượng tách ra MTU con trước khi tạo NC (hao hụt). Rỗng hoặc 0: không tách.</summary>
    public decimal? SplitQuantity { get; set; }

    /// <summary>Đơn vị của lượng tách; so với đơn vị của MTU phân biệt hoa thường (lab L12).</summary>
    public string? SplitUnitOfMeasure { get; set; }

    public string? ScrapCode { get; set; }

    public string? ProblemCode { get; set; }

    public string? ModelCode { get; set; }

    public string? CreatingCostCenter { get; set; }

    public string? ChargeToCostCenter { get; set; }
}

/// <summary>Thông tin team gắn lên NC của lớp Product: nguồn, mã máy, kết luận chờ, các mã báo scrap.</summary>
[Facet<NvOperationBlock>]
public sealed class NonConformanceDetail : Facet<NonConformance>
{
    /// <summary>Một trong <see cref="NonConformanceOrigins"/>. Chỉ NC từ tin máy mới bị tin OK đóng.</summary>
    public string? Origin { get; set; }

    public string? Serial { get; set; }

    /// <summary>MTU mà tin báo (MTU mẹ khi NC nằm trên MTU con tách ra).</summary>
    public string? SourceMtuNId { get; set; }

    public string? OperationCode { get; set; }

    /// <summary>Mã trạng thái máy; <c>FM</c>/<c>TM</c> với hàng chạy thử.</summary>
    public string? StatusCode { get; set; }

    /// <summary>Giờ trong tin. Tin OK ở bước kết luận chỉ đóng NC có giờ sớm hơn nó.</summary>
    public DateTimeOffset? EventTime { get; set; }

    public string? DispositionOperationCode { get; set; }

    /// <summary>Kết luận sẽ áp khi tin OK ở <see cref="DispositionOperationCode"/> tới.</summary>
    public string? PendingDisposition { get; set; }

    public string? SupplierNId { get; set; }

    public bool IsSupplierIssue { get; set; }

    public string? CreatingCostCenter { get; set; }

    public string? ChargeToCostCenter { get; set; }

    public string? ScrapCode { get; set; }

    public string? ProblemCode { get; set; }

    public string? ModelCode { get; set; }

    /// <summary><c>SC</c> + số đếm cơ số 36 đủ 6 ký tự, cấp khi kết luận SCRAP bằng tay.</summary>
    public string? ScrapTag { get; set; }

    public string? Notes { get; set; }
}

public static class NonConformanceOrigins
{
    public const string Line = "Line";
    public const string Shrink = "Shrink";
    public const string Suspect = "Suspect";
    public const string Bulk = "Bulk";
    public const string Manual = "Manual";
}

/// <summary>Dấu chạy thử trên MTU: <c>TM</c> (trial mode, class 2) hoặc <c>FM</c> (flow mode, class 3). Truyền theo genealogy.</summary>
[Facet<NvOperationBlock>]
public sealed class MtuRunMode : Facet<Mtu>
{
    public string? Mode { get; set; }
}

public static class RunModes
{
    public const string Trial = "TM";
    public const string Flow = "FM";

    /// <summary>Cột chế độ của tin: 2 trial, 3 flow, còn lại hàng thường.</summary>
    public static string? FromClass(int? value) => value switch
    {
        2 => Trial,
        3 => Flow,
        _ => null,
    };
}

/// <summary>Bộ đếm của team (scrap tag, mã NCM/PCR). NId là tên bộ đếm, <see cref="Next"/> là số sẽ cấp.</summary>
[Entity<NvOperationBlock>]
public sealed class NvCounter : Entity
{
    public long Next { get; set; }
}

internal static class Counters
{
    public static long Take(IPlatform platform, string name)
    {
        var counter = platform.Query<NvCounter>().SingleOrDefault(c => c.NId == name);
        if (counter is null)
        {
            counter = platform.Create<NvCounter>();
            counter.NId = name;
        }

        var value = counter.Next;
        counter.Next = value + 1;
        platform.Submit(counter);
        return value;
    }

    /// <summary>Scrap tag: <c>SC</c> + số đếm (bắt đầu từ 1) viết cơ số 36, chữ hoa, đệm 0 đủ 6 ký tự.</summary>
    public static string ScrapTag(IPlatform platform) => "SC" + Base36(Take(platform, "ScrapTag") + 1).PadLeft(6, '0');

    internal static string Base36(long value)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var text = string.Empty;
        do
        {
            text = digits[(int)(value % 36)] + text;
            value /= 36;
        }
        while (value > 0);

        return text;
    }
}

/// <summary>Các phép đọc dùng chung của phần hàng lỗi.</summary>
internal static class QualityData
{
    public static NonConformanceDetail? Detail(IReadOnlyPlatform platform, Guid ncId) =>
        platform.Query<NonConformanceDetail>().SingleOrDefault(d => d.EntityId == ncId);

    public static NonConformanceDetail DetailOrNew(IPlatform platform, NonConformance nc)
    {
        var detail = Detail(platform, nc.Id);
        if (detail is null)
        {
            detail = platform.Create<NonConformanceDetail>();
            detail.EntityId = nc.Id;
        }

        return detail;
    }

    /// <summary>Nhà cung cấp của material; chỉ khi material có đúng một nhà cung cấp.</summary>
    public static string? Supplier(IReadOnlyPlatform platform, string? materialNId)
    {
        var suppliers = platform.ProjectionQuery<MaterialSupplier>().Where(s => s.MaterialNId == materialNId).Select(s => s.SupplierNId).Take(2).ToList();
        return suppliers.Count == 1 ? suppliers[0] : null;
    }

    public static bool CodeExists(IReadOnlyPlatform platform, string kind, string? code) =>
        code is not null && platform.ProjectionQuery<QualityCode>().Any(c => c.NId == QualityCode.NIdOf(kind, code));

    /// <summary>Tên NC tự sinh: <c>NC_&lt;MTU&gt;_&lt;mili giây epoch&gt;</c>, thêm hậu tố khi trùng trong cùng mili giây.</summary>
    public static string NewNcNId(IPlatform platform, string? mtuNId)
    {
        var stem = "NC_" + (mtuNId is null ? string.Empty : mtuNId + "_")
            + platform.Time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nid = stem;
        for (var i = 1; platform.Query<NonConformance>().Any(n => n.NId == nid); i++)
        {
            nid = stem + "_" + i.ToString(CultureInfo.InvariantCulture);
        }

        return nid;
    }

    /// <summary>Thiết bị <paramref name="equipmentNId"/> có nằm dưới (hoặc chính là) <paramref name="areaNId"/> không.</summary>
    public static bool IsUnder(IReadOnlyPlatform platform, string? equipmentNId, string? areaNId)
    {
        if (equipmentNId is null || areaNId is null)
        {
            return false;
        }

        var parents = platform.ProjectionQuery<Equipment>().ToDictionary(e => e.NId, e => e.ParentNId, StringComparer.Ordinal);
        for (var current = equipmentNId; current is not null; current = parents.GetValueOrDefault(current))
        {
            if (current == areaNId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>MTU làm ra từ <paramref name="mtuNId"/> (thành phần của chúng có nó), đệ quy xuôi genealogy.</summary>
    public static List<string> Downstream(IReadOnlyPlatform platform, string mtuNId)
    {
        var found = new List<string>();
        var queue = new Queue<string>([mtuNId]);
        while (queue.TryDequeue(out var current))
        {
            foreach (var product in platform.Query<MaterialConsumption>().Where(c => c.ComponentMtuNId == current).Select(c => c.ProductMtuNId!).Distinct().ToList())
            {
                if (!found.Contains(product) && product != mtuNId)
                {
                    found.Add(product);
                    queue.Enqueue(product);
                }
            }
        }

        return found;
    }

    /// <summary>Thành phần của <paramref name="mtuNId"/>, đệ quy ngược genealogy, kèm cấp (1 là thành phần trực tiếp).</summary>
    public static List<(string MtuNId, string ParentNId, int Level)> Upstream(IReadOnlyPlatform platform, string mtuNId)
    {
        var found = new List<(string, string, int)>();
        var queue = new Queue<(string, int)>([(mtuNId, 0)]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { mtuNId };
        while (queue.TryDequeue(out var current))
        {
            foreach (var component in platform.Query<MaterialConsumption>().Where(c => c.ProductMtuNId == current.Item1).Select(c => c.ComponentMtuNId!).Distinct().ToList())
            {
                if (seen.Add(component))
                {
                    found.Add((component, current.Item1, current.Item2 + 1));
                    queue.Enqueue((component, current.Item2 + 1));
                }
            }
        }

        return found;
    }
}

[Command<NvMasterBlock>]
public sealed class CreateQualityCode : ICommand<NvResponse>
{
    [Mandatory]
    public string? Kind { get; init; }

    [Mandatory]
    public string? Code { get; init; }

    [Mandatory]
    public string? Name { get; init; }
}

internal sealed class CreateQualityCodeHandler : ICommandHandler<CreateQualityCode, NvResponse>
{
    public NvResponse Handle(CreateQualityCode command, IPlatform platform)
    {
        if (!QualityCodeKinds.All.Contains(command.Kind))
        {
            return NvResponse.Fail(NvErrors.InvalidQualityCodeKind, $"'{command.Kind}' is not a quality code kind ({string.Join(", ", QualityCodeKinds.All)}).");
        }

        var nid = QualityCode.NIdOf(command.Kind!, command.Code!);
        if (platform.Query<QualityCode>().Any(c => c.NId == nid))
        {
            return NvResponse.Fail(NvErrors.DuplicateQualityCode, $"{command.Kind} {command.Code} already exists.");
        }

        if (platform.Query<QualityCode>().Any(c => c.Kind == command.Kind && c.Name == command.Name))
        {
            return NvResponse.Fail(NvErrors.DuplicateQualityCodeName, $"{command.Kind} named '{command.Name}' already exists.");
        }

        var code = platform.Create<QualityCode>();
        code.NId = nid;
        code.Kind = command.Kind;
        code.Code = command.Code;
        code.Name = command.Name;
        platform.Submit(code);
        return NvResponse.Of(code);
    }
}

[Command<NvMasterBlock>]
public sealed class UpdateQualityCode : ICommand<NvResponse>
{
    [Mandatory]
    public string? Kind { get; init; }

    [Mandatory]
    public string? Code { get; init; }

    [Mandatory]
    public string? Name { get; init; }
}

internal sealed class UpdateQualityCodeHandler : ICommandHandler<UpdateQualityCode, NvResponse>
{
    public NvResponse Handle(UpdateQualityCode command, IPlatform platform)
    {
        var nid = QualityCode.NIdOf(command.Kind ?? string.Empty, command.Code ?? string.Empty);
        var code = platform.Query<QualityCode>().SingleOrDefault(c => c.NId == nid);
        if (code is null)
        {
            return NvResponse.Fail(NvErrors.UnknownQualityCode, $"{command.Kind} {command.Code} does not exist.");
        }

        if (platform.Query<QualityCode>().Any(c => c.Kind == command.Kind && c.Name == command.Name && c.NId != nid))
        {
            return NvResponse.Fail(NvErrors.DuplicateQualityCodeName, $"{command.Kind} named '{command.Name}' already exists.");
        }

        code.Name = command.Name;
        platform.Submit(code);
        return NvResponse.Of(code);
    }
}

[Command<NvMasterBlock>]
public sealed class DeleteQualityCode : ICommand<NvResponse>
{
    [Mandatory]
    public string? Kind { get; init; }

    [Mandatory]
    public string? Code { get; init; }
}

internal sealed class DeleteQualityCodeHandler : ICommandHandler<DeleteQualityCode, NvResponse>
{
    public NvResponse Handle(DeleteQualityCode command, IPlatform platform)
    {
        var nid = QualityCode.NIdOf(command.Kind ?? string.Empty, command.Code ?? string.Empty);
        var code = platform.Query<QualityCode>().SingleOrDefault(c => c.NId == nid);
        if (code is null)
        {
            return NvResponse.Fail(NvErrors.UnknownQualityCode, $"{command.Kind} {command.Code} does not exist.");
        }

        platform.Delete(code);
        return new NvResponse { NId = nid };
    }
}

[Command<NvMasterBlock>]
public sealed class SetMaterialSupplier : ICommand<NvResponse>
{
    [Mandatory]
    public string? MaterialNId { get; init; }

    [Mandatory]
    public string? SupplierNId { get; init; }
}

internal sealed class SetMaterialSupplierHandler : ICommandHandler<SetMaterialSupplier, NvResponse>
{
    public NvResponse Handle(SetMaterialSupplier command, IPlatform platform)
    {
        if (!platform.ProjectionQuery<Material>().Any(m => m.NId == command.MaterialNId))
        {
            return NvResponse.Fail(NvErrors.UnknownMaterial, $"Material {command.MaterialNId} does not exist.");
        }

        var nid = command.MaterialNId + "|" + command.SupplierNId;
        var link = platform.Query<MaterialSupplier>().SingleOrDefault(s => s.NId == nid) ?? platform.Create<MaterialSupplier>();
        link.NId = nid;
        link.MaterialNId = command.MaterialNId;
        link.SupplierNId = command.SupplierNId;
        platform.Submit(link);
        return NvResponse.Of(link);
    }
}

/// <summary>Tạo hoặc sửa (theo NId) một dòng cấu hình hủy theo bước.</summary>
[Command<NvMasterBlock>]
public sealed class SetOperationScrapConfig : ICommand<NvResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? OperationCode { get; init; }

    [Mandatory]
    public string? StatusCode { get; init; }

    [Mandatory]
    public string? ScheduleNId { get; init; }

    public bool IsFlowMode { get; init; }

    public string? AutoDispositionStatus { get; init; }

    public string? DispositionOperationCode { get; init; }

    public decimal? SplitQuantity { get; init; }

    public string? SplitUnitOfMeasure { get; init; }

    public string? ScrapCode { get; init; }

    public string? ProblemCode { get; init; }

    public string? ModelCode { get; init; }

    public string? CreatingCostCenter { get; init; }

    public string? ChargeToCostCenter { get; init; }
}

internal sealed class SetOperationScrapConfigHandler : ICommandHandler<SetOperationScrapConfig, NvResponse>
{
    public NvResponse Handle(SetOperationScrapConfig command, IPlatform platform)
    {
        var disposition = string.IsNullOrEmpty(command.AutoDispositionStatus) ? null : command.AutoDispositionStatus;
        if (disposition is not (null or NonConformanceStatus.Scrap or NonConformanceStatus.UseAsIs))
        {
            return NvResponse.Fail(NvErrors.InvalidScrapConfig,
                $"Auto disposition must be empty, {NonConformanceStatus.Scrap} or {NonConformanceStatus.UseAsIs}, not '{disposition}'.");
        }

        if (!platform.Query<ProductionSchedule>().Any(s => s.NId == command.ScheduleNId))
        {
            return NvResponse.Fail(NvErrors.UnknownSchedule, $"Production schedule {command.ScheduleNId} does not exist.");
        }

        foreach (var code in new[] { command.OperationCode, command.DispositionOperationCode }.Where(c => !string.IsNullOrEmpty(c)))
        {
            if (!platform.ProjectionQuery<LineOperationCode>().Any(o => o.NId == code))
            {
                return NvResponse.Fail(NvErrors.UnknownOperation, $"Line operation code {code} has no mapping.");
            }
        }

        if (command.SplitQuantity is < 0)
        {
            return NvResponse.Fail(NvErrors.InvalidScrapConfig, "Split quantity cannot be negative.");
        }

        var config = platform.Query<OperationScrapConfig>().SingleOrDefault(c => c.NId == command.NId) ?? platform.Create<OperationScrapConfig>();
        config.NId = command.NId!;
        config.OperationCode = command.OperationCode;
        config.StatusCode = command.StatusCode;
        config.ScheduleNId = command.ScheduleNId;
        config.IsFlowMode = command.IsFlowMode;
        config.AutoDispositionStatus = disposition;
        config.DispositionOperationCode = string.IsNullOrEmpty(command.DispositionOperationCode) ? null : command.DispositionOperationCode;
        config.SplitQuantity = command.SplitQuantity is > 0 ? command.SplitQuantity : null;
        config.SplitUnitOfMeasure = command.SplitUnitOfMeasure;
        config.ScrapCode = command.ScrapCode;
        config.ProblemCode = command.ProblemCode;
        config.ModelCode = command.ModelCode;
        config.CreatingCostCenter = command.CreatingCostCenter;
        config.ChargeToCostCenter = command.ChargeToCostCenter;
        platform.Submit(config);
        return NvResponse.Of(config);
    }
}

[Command<NvMasterBlock>]
public sealed class DeleteOperationScrapConfig : ICommand<NvResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class DeleteOperationScrapConfigHandler : ICommandHandler<DeleteOperationScrapConfig, NvResponse>
{
    public NvResponse Handle(DeleteOperationScrapConfig command, IPlatform platform)
    {
        var config = platform.Query<OperationScrapConfig>().SingleOrDefault(c => c.NId == command.NId);
        if (config is null)
        {
            return NvResponse.Fail(NvErrors.InvalidScrapConfig, $"Scrap configuration {command.NId} does not exist.");
        }

        platform.Delete(config);
        return new NvResponse { NId = command.NId };
    }
}

/// <summary>
/// Chọn cấu hình hủy cho một tin: cùng mã bước (ở cột bước hoặc cột bước kết luận), cùng mã trạng thái (dòng chạy thử: mọi
/// mã), cờ chạy thử khớp việc MTU có dấu, ca của PS chứa giờ trong tin và khu của PS chứa máy của tin. Không khớp: null,
/// tức NC chung.
/// </summary>
internal static class ScrapConfigs
{
    public static OperationScrapConfig? Select(IReadOnlyPlatform platform, string operationCode, string statusCode, bool flowMode,
        string? equipmentNId, DateTimeOffset eventTime)
    {
        var candidates = platform.ProjectionQuery<OperationScrapConfig>()
            .Where(c => (c.OperationCode == operationCode || c.DispositionOperationCode == operationCode) && c.IsFlowMode == flowMode)
            .ToList()
            .Where(c => flowMode || c.StatusCode == statusCode)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var scheduleNIds = candidates.Select(c => c.ScheduleNId).Distinct().ToList();
        var schedules = platform.ProjectionQuery<ProductionSchedule>().Where(s => scheduleNIds.Contains(s.NId)).ToList();
        foreach (var config in candidates.OrderBy(c => c.OperationCode == operationCode ? 0 : 1).ThenBy(c => c.NId, StringComparer.Ordinal))
        {
            var schedule = schedules.SingleOrDefault(s => s.NId == config.ScheduleNId);
            if (schedule is not null && QualityData.IsUnder(platform, equipmentNId, schedule.AreaEquipmentNId)
                && ShiftTime.InWindow(eventTime, ShiftTime.Minutes(schedule.StartTime), ShiftTime.Minutes(schedule.EndTime)))
            {
                return config;
            }
        }

        return null;
    }
}
