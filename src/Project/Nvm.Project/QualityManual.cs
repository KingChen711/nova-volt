using System.Globalization;
using System.Text.RegularExpressions;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Hàng lỗi do người tạo và kết luận: Suspect/Hold ở xưởng, NC và hủy hàng loạt theo danh sách, kết luận trên màn NC, thêm MTU
// vào NC, khai NC theo bước WO, bước và lý do của NC, mã NCM/PCR (cả cho containment request).

/// <summary>
/// Giữ một MTU nghi lỗi ngay tại xưởng: NC loại QUALITY, mức MEDIUM, tên <c>NC_&lt;mili giây&gt;</c>, MTU OnHold. Defect có cờ tự
/// hủy thì NC SCRAP và MTU Scrapped. MTU đã Scrapped bị từ chối; lý do nằm trong thân lỗi (lab L13).
/// </summary>
[Command<NvOperationBlock>]
public sealed class SuspectHoldMtu : ICommand<NvResponse>
{
    [Mandatory]
    public string? MtuNId { get; init; }

    [Mandatory]
    public string? DefectNId { get; init; }

    public string? Notes { get; init; }
}

internal sealed class SuspectHoldMtuHandler : ICommandHandler<SuspectHoldMtu, NvResponse>
{
    public NvResponse Handle(SuspectHoldMtu command, IPlatform platform)
    {
        var mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == command.MtuNId);
        if (mtu is null)
        {
            return NvResponse.Fail(NvErrors.UnknownMtu, $"MTU {command.MtuNId} does not exist.");
        }

        if (mtu.Status == MtuStatus.Scrapped)
        {
            return NvResponse.Fail(NvErrors.MtuScrapped, $"MTU {mtu.NId} is already scrapped; a suspect hold cannot be declared on it.");
        }

        var defect = platform.ProjectionQuery<Defect>().SingleOrDefault(d => d.NId == command.DefectNId);
        if (defect is null)
        {
            return NvResponse.Fail(NvErrors.UnknownDefect, $"Defect {command.DefectNId} does not exist.");
        }

        var status = defect.AutoScrap ? NonConformanceStatus.Scrap : NonConformanceStatus.Open;
        var created = LineNonConformances.Create(platform, mtu, status, defect.NId, null, d =>
        {
            d.Origin = NonConformanceOrigins.Suspect;
            d.SourceMtuNId = mtu.NId;
            d.Serial = mtu.Code;
            d.Notes = command.Notes;
        }, nid: QualityData.NewNcNId(platform, null), severity: "MEDIUM");
        return created.Nc is { } nc ? NvResponse.Of(nc) : NvResponse.Fail(NvErrors.MtuScrapped, created.Error!);
    }
}

public sealed record MtuListError(int Row, string? MtuNId, string Reason);

public sealed class MtuListResponse : CommandResponse
{
    public string? NId { get; set; }

    public IList<MtuListError> Errors { get; } = [];
}

/// <summary>Luật kiểm danh sách MTU của NC và hủy hàng loạt. Một dòng sai thì cả danh sách bị từ chối.</summary>
internal static class MtuLists
{
    public static List<MtuListError> Check(IReadOnlyPlatform platform, string? areaNId, IReadOnlyList<string> mtuNIds)
    {
        var errors = new List<MtuListError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? material = null;
        for (var i = 0; i < mtuNIds.Count; i++)
        {
            var nid = mtuNIds[i];
            var row = i + 1;
            if (!seen.Add(nid))
            {
                errors.Add(new MtuListError(row, nid, "Duplicate row."));
                continue;
            }

            var mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == nid);
            if (mtu is null)
            {
                errors.Add(new MtuListError(row, nid, "MTU does not exist."));
                continue;
            }

            material ??= mtu.MaterialNId;
            if (mtu.Quantity <= 0)
            {
                errors.Add(new MtuListError(row, nid, "Quantity is 0."));
            }
            else if (mtu.Status == MtuStatus.Scrapped)
            {
                errors.Add(new MtuListError(row, nid, "MTU is scrapped."));
            }
            else if (!QualityData.IsUnder(platform, mtu.EquipmentNId, areaNId))
            {
                errors.Add(new MtuListError(row, nid, $"Equipment {mtu.EquipmentNId ?? "N/A"} is not in area {areaNId}."));
            }
            else if (mtu.MaterialNId != material)
            {
                errors.Add(new MtuListError(row, nid, $"Material {mtu.MaterialNId} differs from {material} on the first row."));
            }
        }

        return errors;
    }

    public static MtuListResponse Rejected(List<MtuListError> errors)
    {
        var response = new MtuListResponse();
        foreach (var error in errors)
        {
            response.Errors.Add(error);
        }

        response.SetError(NvErrors.InvalidMtuList, $"{errors.Count} row(s) are not valid: "
            + string.Join("; ", errors.Select(e => $"row {e.Row} {e.MtuNId}: {e.Reason}")));
        return response;
    }
}

/// <summary>Kiểm một danh sách MTU (quét hoặc nạp file) trước khi tạo NC hay hủy hàng loạt; không ghi gì.</summary>
[Command<NvOperationBlock>]
public sealed class ValidateMtuList : ICommand<MtuListResponse>
{
    [Mandatory]
    public string? AreaEquipmentNId { get; init; }

    [Mandatory]
    public IReadOnlyList<string>? MtuNIds { get; init; }
}

internal sealed class ValidateMtuListHandler : ICommandHandler<ValidateMtuList, MtuListResponse>
{
    public MtuListResponse Handle(ValidateMtuList command, IPlatform platform)
    {
        var errors = MtuLists.Check(platform, command.AreaEquipmentNId, command.MtuNIds!);
        return errors.Count == 0 ? new MtuListResponse() : MtuLists.Rejected(errors);
    }
}

/// <summary>
/// Một NC cho cả danh sách. <see cref="Status"/> rỗng: NC OPEN, MTU OnHold (Bulk NC). <c>SCRAP</c>: NC mới SCRAP, MTU
/// Scrapped (hủy hàng loạt); NC OPEN đã có của MTU vẫn để mở.
/// </summary>
[Command<NvOperationBlock>]
public sealed class CreateBulkNonConformance : ICommand<MtuListResponse>
{
    [Mandatory]
    public string? AreaEquipmentNId { get; init; }

    [Mandatory]
    public IReadOnlyList<string>? MtuNIds { get; init; }

    public string? Status { get; init; }

    public string? Notes { get; init; }
}

internal sealed class CreateBulkNonConformanceHandler : ICommandHandler<CreateBulkNonConformance, MtuListResponse>
{
    public MtuListResponse Handle(CreateBulkNonConformance command, IPlatform platform)
    {
        var errors = MtuLists.Check(platform, command.AreaEquipmentNId, command.MtuNIds!);
        if (errors.Count > 0)
        {
            return MtuLists.Rejected(errors);
        }

        var nid = QualityData.NewNcNId(platform, command.MtuNIds![0]);
        var created = platform.CallCommand<CreateNonConformance, ProductResponse>(new CreateNonConformance
        {
            NId = nid,
            MtuNIds = command.MtuNIds,
            Status = string.IsNullOrEmpty(command.Status) ? NonConformanceStatus.Open : command.Status,
            Type = "QUALITY",
        });
        if (!created.Succeeded)
        {
            var failed = new MtuListResponse();
            failed.SetError(created.Error!.ErrorCode, created.Error.ErrorMessage);
            return failed;
        }

        var nc = platform.Query<NonConformance>().Single(n => n.NId == nid);
        var detail = QualityData.DetailOrNew(platform, nc);
        detail.Origin = NonConformanceOrigins.Bulk;
        detail.Notes = command.Notes;
        platform.Submit(detail);
        return new MtuListResponse { NId = nid };
    }
}

/// <summary>
/// Kết luận một hay nhiều NC (màn NC, nút đổi trạng thái). SCRAP bắt buộc cost center tạo, cost center chịu phí, scrap code,
/// problem code, model code (đều phải có trong danh mục) và cấp scrap tag; USE AS IS thả MTU khi MTU hết NC mở. NC đã kết
/// luận (có ngày kết thúc) bị từ chối. Kiểm hết rồi mới ghi.
/// </summary>
[Command<NvOperationBlock>]
public sealed class SentenceNonConformances : ICommand<NvResponse>
{
    [Mandatory]
    public IReadOnlyList<string>? NIds { get; init; }

    [Mandatory]
    public string? Status { get; init; }

    public string? SupplierNId { get; init; }

    public bool IsSupplierIssue { get; init; }

    public string? CreatingCostCenter { get; init; }

    public string? ChargeToCostCenter { get; init; }

    public string? ScrapCode { get; init; }

    public string? ProblemCode { get; init; }

    public string? ModelCode { get; init; }

    public string? Notes { get; init; }
}

internal sealed class SentenceNonConformancesHandler : ICommandHandler<SentenceNonConformances, NvResponse>
{
    public NvResponse Handle(SentenceNonConformances command, IPlatform platform)
    {
        if (command.Status is not (NonConformanceStatus.Scrap or NonConformanceStatus.UseAsIs))
        {
            return NvResponse.Fail(NvErrors.InvalidDisposition, $"Status must be {NonConformanceStatus.Scrap} or {NonConformanceStatus.UseAsIs}.");
        }

        if (command.Status == NonConformanceStatus.Scrap && ScrapFieldsError(platform, command) is { } invalid)
        {
            return invalid;
        }

        var ncs = new List<NonConformance>();
        foreach (var nid in command.NIds!)
        {
            var nc = platform.Query<NonConformance>().SingleOrDefault(n => n.NId == nid);
            if (nc is null)
            {
                return NvResponse.Fail(NvErrors.UnknownNonConformance, $"Non-conformance {nid} does not exist.");
            }

            if (nc.EndDate is not null)
            {
                return NvResponse.Fail(NvErrors.NonConformanceClosed, $"Non-conformance {nid} is already {nc.Status} since {nc.EndDate:O}.");
            }

            ncs.Add(nc);
        }

        foreach (var nc in ncs)
        {
            var result = platform.CallCommand<SetNonConformanceStatus, ProductResponse>(new SetNonConformanceStatus { NId = nc.NId, Status = command.Status });
            if (!result.Succeeded)
            {
                return NvResponse.Fail(result.Error!.ErrorCode, result.Error.ErrorMessage);
            }

            var detail = QualityData.DetailOrNew(platform, nc);
            detail.Origin ??= NonConformanceOrigins.Manual;
            detail.Notes = command.Notes ?? detail.Notes;
            if (command.Status == NonConformanceStatus.Scrap)
            {
                detail.SupplierNId = command.SupplierNId ?? detail.SupplierNId;
                detail.IsSupplierIssue = command.IsSupplierIssue;
                detail.CreatingCostCenter = command.CreatingCostCenter;
                detail.ChargeToCostCenter = command.ChargeToCostCenter;
                detail.ScrapCode = command.ScrapCode;
                detail.ProblemCode = command.ProblemCode;
                detail.ModelCode = command.ModelCode;
                detail.ScrapTag ??= Counters.ScrapTag(platform);
            }

            platform.Submit(detail);
        }

        return new NvResponse { NId = string.Join(",", command.NIds!) };
    }

    private static NvResponse? ScrapFieldsError(IReadOnlyPlatform platform, SentenceNonConformances command)
    {
        var required = new (string Field, string Kind, string? Value)[]
        {
            (nameof(command.CreatingCostCenter), QualityCodeKinds.CostCenter, command.CreatingCostCenter),
            (nameof(command.ChargeToCostCenter), QualityCodeKinds.CostCenter, command.ChargeToCostCenter),
            (nameof(command.ScrapCode), QualityCodeKinds.ScrapCode, command.ScrapCode),
            (nameof(command.ProblemCode), QualityCodeKinds.ProblemCode, command.ProblemCode),
            (nameof(command.ModelCode), QualityCodeKinds.ModelCode, command.ModelCode),
        };
        foreach (var (field, kind, value) in required)
        {
            if (string.IsNullOrEmpty(value))
            {
                return NvResponse.Fail(NvErrors.MissingDispositionField, $"{field} is required to scrap.");
            }

            if (!QualityData.CodeExists(platform, kind, value))
            {
                return NvResponse.Fail(NvErrors.UnknownQualityCode, $"{kind} {value} does not exist.");
            }
        }

        return null;
    }
}

/// <summary>Thêm một MTU vào NC đang mở: MTU OnHold.</summary>
[Command<NvOperationBlock>]
public sealed class AddMtuToNonConformance : ICommand<NvResponse>
{
    [Mandatory]
    public string? NonConformanceNId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class AddMtuToNonConformanceHandler : ICommandHandler<AddMtuToNonConformance, NvResponse>
{
    public NvResponse Handle(AddMtuToNonConformance command, IPlatform platform)
    {
        var nc = platform.Query<NonConformance>().SingleOrDefault(n => n.NId == command.NonConformanceNId);
        if (nc is null)
        {
            return NvResponse.Fail(NvErrors.UnknownNonConformance, $"Non-conformance {command.NonConformanceNId} does not exist.");
        }

        if (nc.Status != NonConformanceStatus.Open)
        {
            return NvResponse.Fail(NvErrors.NonConformanceClosed, $"Non-conformance {nc.NId} is {nc.Status}.");
        }

        var mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == command.MtuNId);
        if (mtu is null)
        {
            return NvResponse.Fail(NvErrors.UnknownMtu, $"MTU {command.MtuNId} does not exist.");
        }

        if (mtu.Status == MtuStatus.Scrapped)
        {
            return NvResponse.Fail(NvErrors.MtuScrapped, $"MTU {mtu.NId} is already scrapped.");
        }

        var itemNId = nc.NId + "|" + mtu.NId;
        if (!platform.Query<NonConformanceItem>().Any(i => i.NId == itemNId))
        {
            var item = platform.Create<NonConformanceItem>();
            item.NId = itemNId;
            item.NonConformanceNId = nc.NId;
            item.MtuNId = mtu.NId;
            platform.Submit(item);
        }

        mtu.Status = MtuStatus.OnHold;
        platform.Submit(mtu);
        return NvResponse.Of(nc);
    }
}

/// <summary>Khai NC trên một bước WO: mọi MTU sản phẩm đã qua bước đó bị giữ dưới một NC OPEN.</summary>
[Command<NvOperationBlock>]
public sealed class TraceNonConformance : ICommand<NvResponse>
{
    [Mandatory]
    public string? WorkOrderOperationNId { get; init; }

    public string? Notes { get; init; }
}

internal sealed class TraceNonConformanceHandler : ICommandHandler<TraceNonConformance, NvResponse>
{
    public NvResponse Handle(TraceNonConformance command, IPlatform platform)
    {
        var step = platform.Query<WorkOrderOperation>().SingleOrDefault(o => o.NId == command.WorkOrderOperationNId);
        if (step is null)
        {
            return NvResponse.Fail(NvErrors.UnknownOperation, $"Work order operation {command.WorkOrderOperationNId} does not exist.");
        }

        var products = platform.Query<LineExecution>().Where(e => e.WorkOrderOperationNId == step.NId).Select(e => e.ProductMtuNId!).Distinct().ToList();
        var mtus = platform.Query<Mtu>().Where(m => products.Contains(m.NId) && m.Status != MtuStatus.Scrapped).Select(m => m.NId).ToList();
        if (mtus.Count == 0)
        {
            return NvResponse.Fail(NvErrors.UnknownMtu, $"No product MTU went through {step.NId}.");
        }

        var nid = QualityData.NewNcNId(platform, null);
        var created = platform.CallCommand<CreateNonConformance, ProductResponse>(new CreateNonConformance
        {
            NId = nid,
            MtuNIds = mtus,
            Type = "QUALITY",
            WorkOrderNId = step.WorkOrderNId,
            OperationNId = step.OperationNId,
        });
        if (!created.Succeeded)
        {
            return NvResponse.Fail(created.Error!.ErrorCode, created.Error.ErrorMessage);
        }

        var nc = platform.Query<NonConformance>().Single(n => n.NId == nid);
        var detail = QualityData.DetailOrNew(platform, nc);
        detail.Origin = NonConformanceOrigins.Manual;
        detail.Notes = command.Notes;
        platform.Submit(detail);
        return NvResponse.Of(nc);
    }
}

/// <summary>Bước phía xưởng gắn với một NC. NId = <c>&lt;NC&gt;|&lt;mã bước&gt;</c>.</summary>
[Entity<NvOperationBlock>]
public sealed class NonConformanceOperation : Entity
{
    [Mandatory]
    public string? NonConformanceNId { get; set; }

    [Mandatory]
    public string? OperationCode { get; set; }
}

/// <summary>Lý do gắn với một NC (danh sách, khác problem code là một giá trị). NId = <c>&lt;NC&gt;|&lt;mã lý do&gt;</c>.</summary>
[Entity<NvOperationBlock>]
public sealed class NonConformanceReasonLink : Entity
{
    [Mandatory]
    public string? NonConformanceNId { get; set; }

    [Mandatory]
    public string? ReasonCode { get; set; }
}

/// <summary>Gắn (<see cref="Remove"/> tắt) hoặc gỡ một bước phía xưởng khỏi NC.</summary>
[Command<NvOperationBlock>]
public sealed class SetNonConformanceOperation : ICommand<NvResponse>
{
    [Mandatory]
    public string? NonConformanceNId { get; init; }

    [Mandatory]
    public string? OperationCode { get; init; }

    public bool Remove { get; init; }
}

internal sealed class SetNonConformanceOperationHandler : ICommandHandler<SetNonConformanceOperation, NvResponse>
{
    public NvResponse Handle(SetNonConformanceOperation command, IPlatform platform)
    {
        if (!platform.Query<NonConformance>().Any(n => n.NId == command.NonConformanceNId))
        {
            return NvResponse.Fail(NvErrors.UnknownNonConformance, $"Non-conformance {command.NonConformanceNId} does not exist.");
        }

        var nid = command.NonConformanceNId + "|" + command.OperationCode;
        var link = platform.Query<NonConformanceOperation>().SingleOrDefault(l => l.NId == nid);
        if (command.Remove)
        {
            if (link is null)
            {
                return NvResponse.Fail(NvErrors.UnknownOperation, $"Operation {command.OperationCode} is not on {command.NonConformanceNId}.");
            }

            platform.Delete(link);
            return new NvResponse { NId = nid };
        }

        if (!platform.ProjectionQuery<LineOperationCode>().Any(o => o.NId == command.OperationCode))
        {
            return NvResponse.Fail(NvErrors.UnknownOperation, $"Line operation code {command.OperationCode} has no mapping.");
        }

        link ??= platform.Create<NonConformanceOperation>();
        link.NId = nid;
        link.NonConformanceNId = command.NonConformanceNId;
        link.OperationCode = command.OperationCode;
        platform.Submit(link);
        return NvResponse.Of(link);
    }
}

[Command<NvOperationBlock>]
public sealed class LinkNonConformanceReason : ICommand<NvResponse>
{
    [Mandatory]
    public string? NonConformanceNId { get; init; }

    [Mandatory]
    public string? ReasonCode { get; init; }
}

internal sealed class LinkNonConformanceReasonHandler : ICommandHandler<LinkNonConformanceReason, NvResponse>
{
    public NvResponse Handle(LinkNonConformanceReason command, IPlatform platform)
    {
        if (!platform.Query<NonConformance>().Any(n => n.NId == command.NonConformanceNId))
        {
            return NvResponse.Fail(NvErrors.UnknownNonConformance, $"Non-conformance {command.NonConformanceNId} does not exist.");
        }

        if (!QualityData.CodeExists(platform, QualityCodeKinds.NonConformanceReason, command.ReasonCode))
        {
            return NvResponse.Fail(NvErrors.UnknownQualityCode, $"{QualityCodeKinds.NonConformanceReason} {command.ReasonCode} does not exist.");
        }

        var nid = command.NonConformanceNId + "|" + command.ReasonCode;
        var link = platform.Query<NonConformanceReasonLink>().SingleOrDefault(l => l.NId == nid) ?? platform.Create<NonConformanceReasonLink>();
        link.NId = nid;
        link.NonConformanceNId = command.NonConformanceNId;
        link.ReasonCode = command.ReasonCode;
        platform.Submit(link);
        return NvResponse.Of(link);
    }
}

/// <summary>
/// Mã tăng kế tiếp của một vụ chất lượng: <c>&lt;loại&gt;_&lt;số&gt;-&lt;lần&gt;</c>, lần đầu <c>-0</c>. Bộ đếm theo (loại, số), dùng
/// chung cho NC và containment request.
/// </summary>
[Command<NvOperationBlock>]
public sealed class NextQualityIdentifier : ICommand<NvResponse>
{
    /// <summary><c>NCM</c> hoặc <c>PCR</c>.</summary>
    [Mandatory]
    public string? Type { get; init; }

    [Mandatory]
    public string? Number { get; init; }
}

internal sealed class NextQualityIdentifierHandler : ICommandHandler<NextQualityIdentifier, NvResponse>
{
    public NvResponse Handle(NextQualityIdentifier command, IPlatform platform) =>
        QualityIdentifiers.Next(platform, command.Type!, command.Number!) is { } identifier
            ? new NvResponse { NId = identifier }
            : NvResponse.Fail(NvErrors.InvalidIdentifierType, $"Identifier type must be NCM or PCR, not '{command.Type}'.");
}

internal static partial class QualityIdentifiers
{
    public static string? Next(IPlatform platform, string type, string number)
    {
        if (type is not ("NCM" or "PCR"))
        {
            return null;
        }

        var counter = type + "_" + number;
        return counter + "-" + Counters.Take(platform, counter).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Mã trong tên: <c>NCM_&lt;số&gt;</c>, không có thì <c>PCR_&lt;số&gt;</c>.</summary>
    public static (string Type, string Number)? Find(string? name)
    {
        foreach (var type in new[] { "NCM", "PCR" })
        {
            var match = Code().Match(name ?? string.Empty);
            while (match.Success)
            {
                if (match.Groups["type"].Value == type)
                {
                    return (type, match.Groups["number"].Value);
                }

                match = match.NextMatch();
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<type>NCM|PCR)_(?<number>\d+)")]
    private static partial Regex Code();
}

/// <summary>Mã NCM/PCR của một containment request, do team đặt lúc nhập.</summary>
[Facet<NvOperationBlock>]
public sealed class ContainmentIdentifier : Facet<ContainmentRequest>
{
    public string? Identifier { get; set; }
}

/// <summary>
/// Containment request vừa nhập: tìm <c>NCM_&lt;số&gt;</c> (rồi <c>PCR_&lt;số&gt;</c>) trong tên, đổi tên CR thành mã tăng kế
/// tiếp và lưu facet. Tên không có mã: code của team định ghi chú vào Notes nhưng không lưu; ở đây chỉ còn dòng trace.
/// </summary>
[EventSubscriber<NvOperationBlock>(Subscription = "ContainmentIdentifier", Predefined = true)]
public sealed class ContainmentIdentifierHandler : IEventSubscriber<ContainmentRequestImported>
{
    public void Handle(ContainmentRequestImported domainEvent, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(platform);
        var request = platform.Query<ContainmentRequest>().SingleOrDefault(c => c.Id == domainEvent.ContainmentRequestId);
        if (request is null)
        {
            return;
        }

        if (QualityIdentifiers.Find(domainEvent.Name) is not { } code)
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Containment request {request.NId}: No NCM/PCR found in '{domainEvent.Name}'.");
            return;
        }

        var identifier = QualityIdentifiers.Next(platform, code.Type, code.Number)!;
        request.Name = identifier;
        platform.Submit(request);
        var facet = platform.Query<ContainmentIdentifier>().SingleOrDefault(f => f.EntityId == request.Id) ?? platform.Create<ContainmentIdentifier>();
        facet.EntityId = request.Id;
        facet.Identifier = identifier;
        platform.Submit(facet);
        platform.Tracer.Write(TraceLevel.Information, $"Containment request {request.NId} is now {identifier}.");
    }
}

/// <summary>Đặt nhà cung cấp của NC: lấy theo tham số, không có thì theo material của MTU (phải có đúng một).</summary>
[Command<NvOperationBlock>]
public sealed class SetNonConformanceSupplier : ICommand<NvResponse>
{
    [Mandatory]
    public string? NonConformanceNId { get; init; }

    public string? SupplierNId { get; init; }

    public bool IsSupplierIssue { get; init; }
}

internal sealed class SetNonConformanceSupplierHandler : ICommandHandler<SetNonConformanceSupplier, NvResponse>
{
    public NvResponse Handle(SetNonConformanceSupplier command, IPlatform platform)
    {
        var nc = platform.Query<NonConformance>().SingleOrDefault(n => n.NId == command.NonConformanceNId);
        if (nc is null)
        {
            return NvResponse.Fail(NvErrors.UnknownNonConformance, $"Non-conformance {command.NonConformanceNId} does not exist.");
        }

        var supplier = command.SupplierNId;
        if (string.IsNullOrEmpty(supplier))
        {
            var mtuNId = platform.Query<NonConformanceItem>().Where(i => i.NonConformanceNId == nc.NId).Select(i => i.MtuNId).FirstOrDefault();
            var material = platform.Query<Mtu>().Where(m => m.NId == mtuNId).Select(m => m.MaterialNId).SingleOrDefault();
            supplier = QualityData.Supplier(platform, material);
            if (supplier is null)
            {
                return NvResponse.Fail(NvErrors.MissingDispositionField, $"Material {material} has no single supplier; give one.");
            }
        }

        var detail = QualityData.DetailOrNew(platform, nc);
        detail.SupplierNId = supplier;
        detail.IsSupplierIssue = command.IsSupplierIssue;
        platform.Submit(detail);
        return NvResponse.Of(nc);
    }
}

/// <summary>
/// Khai NC nhà cung cấp (composite của app): giữ MTU bằng Suspect/Hold, rồi đặt nhà cung cấp. Mỗi bước một transaction:
/// bước hai lỗi thì response báo lỗi nhưng NC và MTU OnHold của bước một đã ghi (lab L01).
/// </summary>
[AppCommand(NvProductionExtApp.Name)]
public sealed class DeclareSupplierIssue : ICommand<NvResponse>
{
    [Mandatory]
    public string? MtuNId { get; init; }

    [Mandatory]
    public string? DefectNId { get; init; }

    public string? SupplierNId { get; init; }
}

public sealed class DeclareSupplierIssueHandler : ICompositeCommandHandler<DeclareSupplierIssue, NvResponse>
{
    public NvResponse Handle(DeclareSupplierIssue command, ICompositePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(platform);
        var hold = platform.CallCommand<SuspectHoldMtu, NvResponse>(new SuspectHoldMtu { MtuNId = command.MtuNId, DefectNId = command.DefectNId });
        if (!hold.Succeeded)
        {
            return NvResponse.Fail(hold.Error!.ErrorCode, "The non-conformance could not be created.");
        }

        var supplier = platform.CallCommand<SetNonConformanceSupplier, NvResponse>(
            new SetNonConformanceSupplier { NonConformanceNId = hold.NId, SupplierNId = command.SupplierNId, IsSupplierIssue = true });
        return supplier.Succeeded
            ? hold
            : NvResponse.Fail(supplier.Error!.ErrorCode, "There was an error updating the non-conformance supplier: " + supplier.Error.ErrorMessage);
    }
}
