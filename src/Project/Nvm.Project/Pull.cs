using System.Globalization;
using System.Text.Json;
using Nvm.Foundation;
using Nvm.Product;
using Buffer = Nvm.Product.Buffer;

namespace Nvm.Project;

// Gọi hàng (R10): route bốn tier từ kho tới điểm giao ở line. Khu gọi hàng ở tier 4; mỗi chặng hoàn tất tự sinh yêu cầu bù chặng
// dưới cùng số lượng, không xét tồn kho; chặng lấy từ tier 1 thêm picklist gửi WMS. Tier: 1 kho, 2 chỗ tạm, 3 phòng chuyển tiếp, 4 điểm giao.

[FunctionalBlock("FB_MS_Pull", ModelDomain.MasterData)]
public sealed class PullMasterBlock;

[FunctionalBlock("FB_OP_Pull", ModelDomain.OperationalData)]
public sealed class PullOperationBlock;

/// <summary>App gọi hàng của team. App độc lập.</summary>
[App(Name)]
public sealed class PullApp : AppDefinition
{
    public const string Name = "PullApp";

    protected override void Define(PomBuilder pom) => pom
        .Entity<DeliveryRoute>().Entity<RouteMaterial>().Entity<PullFlowProcess>().Entity<PullTaskLot>().Entity<DirectDeliveryLine>().Entity<LotExpiry>()
        .Entity<Kanban>().Entity<PartAddress>().Entity<UomConversion>().Entity<Buffer>().Entity<BufferWhitelist>().Entity<BufferAssignment>()
        .Entity<LogisticRequest>().Entity<TransportOperation>().Entity<Mtu>()
        .Command<SetDeliveryRoute>().Command<SetRouteMaterial>().Command<CallMaterial>().Command<AcceptPullTask>().Command<RecordLot>()
        .Command<CompletePullTask>().Command<CancelPullTask>().Command<ReceiveDirectDelivery>().Command<ReceiveDirectMaterial>().Command<LoadLot>()
        .Command<UnloadLot>()
        .ReadingFunction<LinkedMaterials>().ReadingFunction<PullTasks>().ReadingFunction<AreaRequests>().ReadingFunction<ScanLot>()
        .ReadingFunction<AndonRoutes>().ReadingFunction<AndonDetail>().ReadingFunction<LoadedLots>().ReadingFunction<SearchLots>();
}

public sealed class PullResponse : CommandResponse
{
    public string? NId { get; set; }

    public string? Status { get; set; }

    internal static PullResponse Fail(int code, string message)
    {
        var response = new PullResponse();
        response.SetError(code, message);
        return response;
    }

    internal static PullResponse From(CommandResponse failed) => Fail(failed.Error!.ErrorCode, failed.Error.ErrorMessage);
}

/// <summary>Adapter của tin gọi hàng; nơi chúng đi tới là cấu hình của tầng tích hợp.</summary>
public static class PullAdapters
{
    public const string Wms = "WMS";
}

/// <summary>Đường đi của một part: buffer bốn tier, tier 3 có thể trống; phút mỗi chặng. Mọi tier đều không bắt buộc trừ tier 4.</summary>
[Entity<PullMasterBlock>]
public sealed class DeliveryRoute : Entity
{
    public string? Area { get; set; }

    public string? Buffer1 { get; set; }

    public string? Buffer2 { get; set; }

    public string? Buffer3 { get; set; }

    [Mandatory]
    public string? Buffer4 { get; set; }

    /// <summary>Phút cho chặng tier 1 → 2.</summary>
    public int Minutes1 { get; set; }

    /// <summary>Phút cho chặng tới tier 3.</summary>
    public int Minutes2 { get; set; }

    /// <summary>Phút cho chặng tới tier 4.</summary>
    public int Minutes3 { get; set; }
}

/// <summary>Material đặt thẳng đi theo route, không có kanban. NId = <c>route|material</c>.</summary>
[Entity<PullMasterBlock>]
public sealed class RouteMaterial : Entity
{
    public string? RouteNId { get; set; }

    public string? MaterialNId { get; set; }

    /// <summary>Số lượng mỗi lần gọi.</summary>
    public decimal Quantity { get; set; }
}

/// <summary>Một yêu cầu đang ở chặng nào của route. NId = NId của yêu cầu; level là tier đích.</summary>
[Entity<PullOperationBlock>]
public sealed class PullFlowProcess : Entity
{
    public string? RouteNId { get; set; }

    public int Level { get; set; }

    /// <summary>Rỗng với material đặt thẳng: lệnh đó không lên bảng gọi hàng.</summary>
    public string? PartAddressNId { get; set; }
}

/// <summary>Lô đã quét cho một việc chở. NId = NId của việc chở, nên mỗi việc một lô.</summary>
[Entity<PullOperationBlock>]
public sealed class PullTaskLot : Entity
{
    [Mandatory]
    public string? MtuNId { get; set; }
}

/// <summary>Dòng đơn nguyên liệu đặt thẳng từ ERP. NId = <c>đơn|dòng</c>.</summary>
[Entity<PullOperationBlock>]
public sealed class DirectDeliveryLine : Entity
{
    public string? PoNo { get; set; }

    public string? PoLine { get; set; }

    public string? MaterialNId { get; set; }

    public string? Batch { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Dạng <c>yyyyMMdd</c>.</summary>
    public string? ExpirationDate { get; set; }
}

/// <summary>Hạn dùng và số đơn của lô đặt thẳng. NId = NId của MTU; lô không có đơn thì không có hạn dùng.</summary>
[Entity<PullOperationBlock>]
public sealed class LotExpiry : Entity
{
    public string? PoNo { get; set; }

    public string? ExpirationDate { get; set; }
}

/// <summary>Nhãn lô đặt thẳng: cắt theo vị trí, số lượng dài tùy đơn nên đếm từ hai đầu.</summary>
public sealed record DirectLabel(string Delivery, string PoNo, string MaterialNId, string Batch, decimal ShipQuantity, string StorageCode)
{
    public const int MinimumLength = 50;

    public static bool TryParse(string? barcode, out DirectLabel label)
    {
        label = null!;
        if (barcode is null || barcode.Length < MinimumLength
            || !decimal.TryParse(barcode[44..^3], NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity))
        {
            return false;
        }

        label = new DirectLabel(barcode[..10].TrimEnd(), barcode[10..20].TrimEnd(), barcode[20..34].TrimEnd(), barcode[34..44].TrimEnd(), quantity, barcode[^3..]);
        return true;
    }

    public static string Build(string delivery, string po, string material, string batch, decimal quantity, string storage = "STO")
    {
        static string Cell(string value, int width) => value.PadRight(width)[..width];
        return Cell(delivery, 10) + Cell(po, 10) + Cell(material, 14) + Cell(batch, 10) + quantity.ToString("000.000", CultureInfo.InvariantCulture) + Cell(storage, 3);
    }
}

internal static class Pull
{
    public static string? Tier(DeliveryRoute route, int tier) => tier switch
    {
        1 => route.Buffer1,
        2 => route.Buffer2,
        3 => route.Buffer3,
        4 => route.Buffer4,
        _ => null,
    };

    /// <summary>Hạn của một chặng tính từ lúc tạo việc chở: chặng tới tier 4 dùng phút thứ ba, tới tier 3 phút thứ hai, tới tier 2 phút đầu.</summary>
    public static string LeadTime(DeliveryRoute route, string? destination, DateTimeOffset created, DateTimeOffset now)
    {
        int? minutes = destination == route.Buffer4 ? route.Minutes3
            : destination == route.Buffer3 ? route.Minutes2
            : destination == route.Buffer2 ? route.Minutes1
            : null;
        return minutes is null ? "NotDefined" : created.AddMinutes(minutes.Value) <= now ? "Late" : "InTime";
    }

    public static string BufferOf(IReadOnlyPlatform platform, string? equipment) =>
        platform.ProjectionQuery<Buffer>().Where(b => b.EquipmentNId == equipment).OrderBy(b => b.NId).Select(b => b.NId).FirstOrDefault()!;

    /// <summary>Gắn lô mới nhận vào buffer của vị trí cất. Vị trí không có buffer: lô không gắn, người chở sẽ không quét ra (chỉ ghi trace).</summary>
    public static void LinkReceived(IPlatform platform, string mtu, string location)
    {
        var buffer = BufferOf(platform, location);
        if (buffer is null)
        {
            platform.Tracer.Write(TraceLevel.Information, $"Location {location} has no buffer: lot {mtu} not linked.");
            return;
        }

        var linked = platform.CallCommand<LinkMtuToBuffer, ProductResponse>(new LinkMtuToBuffer { BufferNId = buffer, MtuNId = mtu });
        if (!linked.Succeeded)
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Lot {mtu} not linked to {buffer}: {linked.Error!.ErrorMessage}");
        }
    }

    public static PullResponse? Task(IPlatform platform, string nid, out TransportOperation task)
    {
        task = platform.Query<TransportOperation>().SingleOrDefault(t => t.NId == nid)!;
        return task is null ? PullResponse.Fail(NvErrors.UnknownPullTask, $"Transport task {nid} does not exist.") : null;
    }

    public static string TaskId(IPlatform platform) => "TW-" + Counters.Base36(Counters.Take(platform, "PullTask") + 1).PadLeft(6, '0');

    public static string RequestId(IPlatform platform, int? level = null) =>
        (level is null ? "PR-" : $"PR-T{level}-") + Counters.Base36(Counters.Take(platform, "PullRequest") + 1).PadLeft(6, '0');

    /// <summary>MTU của một nhãn: nhãn skid ra đúng một tên ghép, nhãn đặt thẳng ra mọi lô cùng batch, chuỗi khác so theo tên chứa chuỗi đó.</summary>
    public static IQueryable<Mtu> Search(IReadingPlatform platform, string? label)
    {
        var mtus = platform.ProjectionQuery<Mtu>();
        if (SkidLabel.TryParse(label, out var skid))
        {
            var name = skid.Supplier + skid.Dock + skid.PoNo + skid.Pallet + skid.Skid;
            return mtus.Where(m => m.NId == name);
        }

        if (DirectLabel.TryParse(label, out var direct))
        {
            var prefix = direct.Batch + "_";
            return mtus.Where(m => m.NId.StartsWith(prefix));
        }

        return string.IsNullOrEmpty(label) ? mtus.Where(m => false) : mtus.Where(m => m.NId.Contains(label));
    }
}

/// <summary>Nhận việc chở ngay khi có yêu cầu, nếu yêu cầu có buffer nguồn. Không có nguồn thì không sinh việc.</summary>
[PostAction<PullOperationBlock>]
public sealed class AutoAcceptLogisticRequest : IPostAction<CreateLogisticRequest, ProductResponse>
{
    public ProductResponse Execute(CreateLogisticRequest command, ProductResponse response, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(platform);
        if (command.SourceBufferNId is null)
        {
            platform.Tracer.Write(TraceLevel.Information, $"Logistic request {command.NId} has no source buffer: no transport task.");
            return response;
        }

        var accepted = platform.CallCommand<AcceptMaterialRequest, ProductResponse>(new AcceptMaterialRequest
        {
            LogisticRequestNId = command.NId,
            TransportOperationNId = Pull.TaskId(platform),
        });
        return accepted.Succeeded ? response : accepted;
    }
}

/// <summary>
/// Hoàn tất một chặng thì sinh yêu cầu bù chặng dưới: level trừ 1, route trống tier 3 thì xuống 2. Lấy từ tier 1 thì thêm picklist gửi
/// WMS, nếu ô đích có mã máy bên WMS. Level 2 xong là hết chuỗi. Cùng material, cùng số lượng; không xét tồn kho.
/// </summary>
[PostAction<PullOperationBlock>]
public sealed class ReplenishNextTier : IPostAction<CompleteTransportOperation, ProductResponse>
{
    public ProductResponse Execute(CompleteTransportOperation command, ProductResponse response, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(platform);
        var task = platform.Query<TransportOperation>().Single(t => t.NId == command.NId);
        var process = platform.Query<PullFlowProcess>().SingleOrDefault(p => p.NId == task.LogisticRequestNId);
        if (process is null)
        {
            return response;
        }

        var route = platform.ProjectionQuery<DeliveryRoute>().Single(r => r.NId == process.RouteNId);
        var level = process.Level - 1;
        if (level == 3 && route.Buffer3 is null)
        {
            level = 2;
        }

        string? source = null;
        var picklist = false;
        if (level == 3)
        {
            source = route.Buffer2 ?? route.Buffer1;
            picklist = route.Buffer2 is null;
        }
        else if (level == 2)
        {
            source = route.Buffer1;
            picklist = true;
        }

        var destination = Pull.Tier(route, level);
        if (source is null || destination is null)
        {
            return response;
        }

        var nid = Pull.RequestId(platform, level);
        var created = platform.CallCommand<CreateLogisticRequest, ProductResponse>(new CreateLogisticRequest
        {
            NId = nid,
            MaterialNId = task.MaterialNId,
            Quantity = task.Quantity,
            SourceBufferNId = source,
            DestinationBufferNId = destination,
        });
        if (!created.Succeeded)
        {
            return created;
        }

        var next = platform.Create<PullFlowProcess>();
        next.NId = nid;
        next.RouteNId = process.RouteNId;
        next.Level = level;
        next.PartAddressNId = process.PartAddressNId;
        platform.Submit(next);

        if (picklist)
        {
            var equipment = platform.ProjectionQuery<Buffer>().Where(b => b.NId == destination).Select(b => b.EquipmentNId).Single();
            var machine = Inbound.Property(platform, equipment, "WmsMachineId");
            if (machine is not null)
            {
                platform.SendMessageAsync(new OutboundMessage(PullAdapters.Wms, "Picklist",
                    JsonSerializer.Serialize(new { machine, material = task.MaterialNId, quantity = task.Quantity, workOrder = nid })));
            }
        }

        return response;
    }
}

/// <summary>Sau khi gắn lô vào buffer, lô tới equipment của buffer đó. Lỗi chỉ ghi trace: lô đã thuộc buffer.</summary>
[PostAction<PullOperationBlock>]
public sealed class MoveLotToBufferEquipment : IPostAction<LinkMtuToBuffer, ProductResponse>
{
    public ProductResponse Execute(LinkMtuToBuffer command, ProductResponse response, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(platform);
        var equipment = platform.ProjectionQuery<Buffer>().Where(b => b.NId == command.BufferNId).Select(b => b.EquipmentNId).Single();
        var moved = platform.CallCommand<MoveMtu, ProductResponse>(new MoveMtu { NId = command.MtuNId, EquipmentNId = equipment });
        if (!moved.Succeeded)
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Lot {command.MtuNId} not moved to {equipment}: {moved.Error!.ErrorMessage}");
        }

        return response;
    }
}

[Command<PullMasterBlock>]
public sealed class SetDeliveryRoute : ICommand<PullResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    public string? Area { get; init; }

    public string? Buffer1 { get; init; }

    public string? Buffer2 { get; init; }

    public string? Buffer3 { get; init; }

    [Mandatory]
    public string? Buffer4 { get; init; }

    public int Minutes1 { get; init; }

    public int Minutes2 { get; init; }

    public int Minutes3 { get; init; }
}

internal sealed class SetDeliveryRouteHandler : ICommandHandler<SetDeliveryRoute, PullResponse>
{
    public PullResponse Handle(SetDeliveryRoute command, IPlatform platform)
    {
        foreach (var buffer in new[] { command.Buffer1, command.Buffer2, command.Buffer3, command.Buffer4 })
        {
            if (!string.IsNullOrEmpty(buffer) && !platform.Query<Buffer>().Any(b => b.NId == buffer))
            {
                return PullResponse.Fail(NvErrors.UnknownRoute, $"Buffer {buffer} does not exist.");
            }
        }

        var route = platform.Query<DeliveryRoute>().SingleOrDefault(r => r.NId == command.NId) ?? platform.Create<DeliveryRoute>();
        route.NId = command.NId!;
        route.Area = command.Area;
        route.Buffer1 = string.IsNullOrEmpty(command.Buffer1) ? null : command.Buffer1;
        route.Buffer2 = string.IsNullOrEmpty(command.Buffer2) ? null : command.Buffer2;
        route.Buffer3 = string.IsNullOrEmpty(command.Buffer3) ? null : command.Buffer3;
        route.Buffer4 = command.Buffer4;
        route.Minutes1 = command.Minutes1;
        route.Minutes2 = command.Minutes2;
        route.Minutes3 = command.Minutes3;
        platform.Submit(route);
        return new PullResponse { NId = route.NId };
    }
}

[Command<PullMasterBlock>]
public sealed class SetRouteMaterial : ICommand<PullResponse>
{
    [Mandatory]
    public string? RouteNId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    public decimal Quantity { get; init; }
}

internal sealed class SetRouteMaterialHandler : ICommandHandler<SetRouteMaterial, PullResponse>
{
    public PullResponse Handle(SetRouteMaterial command, IPlatform platform)
    {
        if (!platform.Query<DeliveryRoute>().Any(r => r.NId == command.RouteNId))
        {
            return PullResponse.Fail(NvErrors.UnknownRoute, $"Route {command.RouteNId} does not exist.");
        }

        var nid = command.RouteNId + "|" + command.MaterialNId;
        var row = platform.Query<RouteMaterial>().SingleOrDefault(r => r.NId == nid) ?? platform.Create<RouteMaterial>();
        row.NId = nid;
        row.RouteNId = command.RouteNId;
        row.MaterialNId = command.MaterialNId;
        row.Quantity = command.Quantity;
        platform.Submit(row);
        return new PullResponse { NId = nid };
    }
}

/// <summary>
/// Gọi hàng ở tier 4: material phải có trong whitelist ở mọi tier của route; nguồn là tier 3, trống thì tier 2. Một lần gọi sinh cả yêu
/// cầu lẫn việc chở. Gấp là priority 1, thường là 2.
/// </summary>
[Command<PullOperationBlock>]
public sealed class CallMaterial : ICommand<PullResponse>
{
    [Mandatory]
    public string? RouteNId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    public decimal Quantity { get; init; }

    public string? PartAddressNId { get; init; }

    public bool Hot { get; init; }
}

internal sealed class CallMaterialHandler : ICommandHandler<CallMaterial, PullResponse>
{
    public PullResponse Handle(CallMaterial command, IPlatform platform)
    {
        var route = platform.ProjectionQuery<DeliveryRoute>().SingleOrDefault(r => r.NId == command.RouteNId);
        if (route is null)
        {
            return PullResponse.Fail(NvErrors.UnknownRoute, $"Route {command.RouteNId} does not exist.");
        }

        for (var tier = 4; tier >= 1; tier--)
        {
            var buffer = Pull.Tier(route, tier);
            if (buffer is not null && !platform.ProjectionQuery<BufferWhitelist>().Any(w => w.BufferNId == buffer && w.MaterialNId == command.MaterialNId))
            {
                return PullResponse.Fail(NvErrors.MaterialNotOnTier, $"Material {command.MaterialNId} is not assigned to buffer {buffer} in tier {tier}.");
            }
        }

        var nid = Pull.RequestId(platform);
        var created = platform.CallCommand<CreateLogisticRequest, ProductResponse>(new CreateLogisticRequest
        {
            NId = nid,
            MaterialNId = command.MaterialNId,
            Quantity = command.Quantity,
            SourceBufferNId = route.Buffer3 ?? route.Buffer2,
            DestinationBufferNId = route.Buffer4,
            Priority = command.Hot ? 1 : 2,
        });
        if (!created.Succeeded)
        {
            return PullResponse.From(created);
        }

        var process = platform.Create<PullFlowProcess>();
        process.NId = nid;
        process.RouteNId = route.NId;
        process.Level = 4;
        process.PartAddressNId = command.PartAddressNId;
        platform.Submit(process);
        return new PullResponse { NId = nid };
    }
}

[Command<PullOperationBlock>]
public sealed class AcceptPullTask : ICommand<PullResponse>
{
    [Mandatory]
    public string? TaskNId { get; init; }
}

internal sealed class AcceptPullTaskHandler : ICommandHandler<AcceptPullTask, PullResponse>
{
    public PullResponse Handle(AcceptPullTask command, IPlatform platform)
    {
        var started = platform.CallCommand<StartTransportOperation, ProductResponse>(new StartTransportOperation { NId = command.TaskNId });
        return started.Succeeded ? new PullResponse { NId = command.TaskNId, Status = TransportStatus.Active } : PullResponse.From(started);
    }
}

/// <summary>Ghi lô đã quét cho việc chở. Chưa dời lô: lô chỉ rời chỗ khi hoàn tất. Lô phải đúng part, ở buffer nguồn và đúng thứ tự hạn dùng.</summary>
[Command<PullOperationBlock>]
public sealed class RecordLot : ICommand<PullResponse>
{
    [Mandatory]
    public string? TaskNId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class RecordLotHandler : ICommandHandler<RecordLot, PullResponse>
{
    public PullResponse Handle(RecordLot command, IPlatform platform)
    {
        if (Pull.Task(platform, command.TaskNId!, out var task) is { } missing)
        {
            return missing;
        }

        if (task.Status is not (TransportStatus.Released or TransportStatus.Active))
        {
            return PullResponse.Fail(NvErrors.InvalidPullTask, $"Transport task {task.NId} is {task.Status}.");
        }

        if (platform.Query<PullTaskLot>().SingleOrDefault(l => l.NId == task.NId) is { } recorded)
        {
            return PullResponse.Fail(NvErrors.LotAlreadyRecorded, $"Transport task {task.NId} already has lot {recorded.MtuNId}.");
        }

        var mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == command.MtuNId);
        if (mtu is null)
        {
            return PullResponse.Fail(ProductErrors.UnknownMtu, $"Lot {command.MtuNId} does not exist.");
        }

        if (mtu.MaterialNId != task.MaterialNId)
        {
            return PullResponse.Fail(NvErrors.LotWrongMaterial, $"Scanned lot {mtu.NId} does not match the required part {task.MaterialNId}.");
        }

        var current = platform.Query<BufferAssignment>().Where(a => a.NId == mtu.NId).Select(a => a.BufferNId).SingleOrDefault();
        if (current != task.SourceBufferNId)
        {
            return PullResponse.Fail(NvErrors.LotNotInSource, $"Lot {mtu.NId} is in buffer {current ?? "(none)"}, not in source buffer {task.SourceBufferNId}.");
        }

        var first = Fefo.First(platform, task);
        if (first is not null && first != mtu.NId)
        {
            return PullResponse.Fail(NvErrors.LotOutsideFefo, $"Lot {mtu.NId} is outside FEFO: use {first} first.");
        }

        var lot = platform.Create<PullTaskLot>();
        lot.NId = task.NId;
        lot.MtuNId = mtu.NId;
        platform.Submit(lot);
        return new PullResponse { NId = task.NId, Status = task.Status };
    }
}

internal static class Fefo
{
    /// <summary>Lô phải lấy trước của một việc chở: trong buffer nguồn và buffer tier 1, hạn dùng sớm nhất, rồi tạo sớm nhất. Không hạn dùng xếp sau.</summary>
    public static string? First(IPlatform platform, TransportOperation task)
    {
        var process = platform.Query<PullFlowProcess>().SingleOrDefault(p => p.NId == task.LogisticRequestNId);
        var route = process is null ? null : platform.ProjectionQuery<DeliveryRoute>().SingleOrDefault(r => r.NId == process.RouteNId);
        return Candidates(platform.Query<BufferAssignment>(), platform.Query<Mtu>(), platform.Query<LotExpiry>(), task, route);
    }

    public static string? First(IReadingPlatform platform, TransportOperation task, DeliveryRoute? route) =>
        Candidates(platform.ProjectionQuery<BufferAssignment>(), platform.ProjectionQuery<Mtu>(), platform.ProjectionQuery<LotExpiry>(), task, route);

    private static string? Candidates(IQueryable<BufferAssignment> assignments, IQueryable<Mtu> mtus, IQueryable<LotExpiry> expiries, TransportOperation task, DeliveryRoute? route)
    {
        var buffers = new[] { task.SourceBufferNId, route?.Buffer1 };
        var lots = assignments.Where(a => buffers.Contains(a.BufferNId)).Select(a => a.MtuNId);
        var rows = mtus.Where(m => lots.Contains(m.NId) && m.MaterialNId == task.MaterialNId).Select(m => new { m.NId, m.CreatedOn }).ToList();
        var names = rows.Select(r => r.NId).ToList();
        var dates = expiries.Where(e => names.Contains(e.NId)).ToDictionary(e => e.NId, e => e.ExpirationDate);
        return rows
            .OrderBy(r => dates.GetValueOrDefault(r.NId) ?? "99991231", StringComparer.Ordinal)
            .ThenBy(r => r.CreatedOn)
            .ThenBy(r => r.NId, StringComparer.Ordinal)
            .Select(r => r.NId)
            .FirstOrDefault();
    }
}

/// <summary>Hoàn tất việc chở đã có lô: lô tới buffer đích; lớp Product dời lô, post-action sinh chặng dưới.</summary>
[Command<PullOperationBlock>]
public sealed class CompletePullTask : ICommand<PullResponse>
{
    [Mandatory]
    public string? TaskNId { get; init; }
}

internal sealed class CompletePullTaskHandler : ICommandHandler<CompletePullTask, PullResponse>
{
    public PullResponse Handle(CompletePullTask command, IPlatform platform)
    {
        if (Pull.Task(platform, command.TaskNId!, out var task) is { } missing)
        {
            return missing;
        }

        var lot = platform.Query<PullTaskLot>().SingleOrDefault(l => l.NId == task.NId);
        if (lot is null)
        {
            return PullResponse.Fail(NvErrors.NoLotRecorded, $"Transport task {task.NId} has no recorded lot.");
        }

        var completed = platform.CallCommand<CompleteTransportOperation, ProductResponse>(new CompleteTransportOperation { NId = task.NId, MtuNId = lot.MtuNId });
        return completed.Succeeded ? new PullResponse { NId = task.NId, Status = TransportStatus.Complete } : PullResponse.From(completed);
    }
}

/// <summary>Hủy lệnh gọi: chỉ khi việc chở còn Released và chưa có lô.</summary>
[Command<PullOperationBlock>]
public sealed class CancelPullTask : ICommand<PullResponse>
{
    [Mandatory]
    public string? TaskNId { get; init; }
}

internal sealed class CancelPullTaskHandler : ICommandHandler<CancelPullTask, PullResponse>
{
    public PullResponse Handle(CancelPullTask command, IPlatform platform)
    {
        if (Pull.Task(platform, command.TaskNId!, out var task) is { } missing)
        {
            return missing;
        }

        if (platform.Query<PullTaskLot>().Any(l => l.NId == task.NId))
        {
            return PullResponse.Fail(NvErrors.InvalidPullTask, $"Transport task {task.NId} already has a lot.");
        }

        var cancelled = platform.CallCommand<CancelTransportOperation, ProductResponse>(new CancelTransportOperation { NId = task.NId });
        return cancelled.Succeeded ? new PullResponse { NId = task.NId, Status = TransportStatus.Cancelled } : PullResponse.From(cancelled);
    }
}

internal sealed record DirectDeliveryRow(string? PoNo, string? PoLine, string? Material, string? Batch, decimal Quantity, string? ExpirationDate);

/// <summary>Đơn nguyên liệu đặt thẳng từ ERP: thêm hoặc sửa theo đơn và dòng. Không bắt buộc phải có đơn mới nhận được, nhưng thiếu đơn thì lô không có hạn dùng.</summary>
[Command<PullOperationBlock>]
public sealed class ReceiveDirectDelivery : ICommand<PullResponse>
{
    [Mandatory]
    public string? Payload { get; init; }
}

internal sealed class ReceiveDirectDeliveryHandler : ICommandHandler<ReceiveDirectDelivery, PullResponse>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public PullResponse Handle(ReceiveDirectDelivery command, IPlatform platform)
    {
        List<DirectDeliveryRow>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<DirectDeliveryRow>>(command.Payload!, Json);
        }
        catch (JsonException e)
        {
            return PullResponse.Fail(NvErrors.InvalidDirectDelivery, "Delivery payload is not valid JSON: " + e.Message);
        }

        if (rows is null || rows.Count == 0)
        {
            return PullResponse.Fail(NvErrors.InvalidDirectDelivery, "Delivery payload has no lines.");
        }

        foreach (var row in rows)
        {
            if (!platform.ProjectionQuery<Material>().Any(m => m.NId == row.Material))
            {
                return PullResponse.Fail(NvErrors.UnknownMaterial, $"Material {row.Material} does not exist.");
            }

            var nid = row.PoNo + "|" + row.PoLine;
            var line = platform.Query<DirectDeliveryLine>().SingleOrDefault(l => l.NId == nid) ?? platform.Create<DirectDeliveryLine>();
            line.NId = nid;
            line.PoNo = row.PoNo;
            line.PoLine = row.PoLine;
            line.MaterialNId = row.Material;
            line.Batch = row.Batch;
            line.Quantity = row.Quantity;
            line.ExpirationDate = row.ExpirationDate;
            platform.Submit(line);
        }

        return new PullResponse { NId = rows[0].PoNo };
    }
}

/// <summary>
/// Nhận nguyên liệu đặt thẳng: cắt nhãn, quy đổi đơn vị theo hệ <c>Receiving</c>, chặn nhận vượt số giao của batch, tạo lô
/// <c>batch_pallet</c> ở vị trí cất, lấy hạn dùng từ đơn theo batch, gắn vào buffer của vị trí.
/// </summary>
[Command<PullOperationBlock>]
public sealed class ReceiveDirectMaterial : ICommand<PullResponse>
{
    [Mandatory]
    public string? Label { get; init; }

    [Mandatory]
    public string? StorageLocation { get; init; }

    [Mandatory]
    public string? PalletNumber { get; init; }

    public decimal Quantity { get; init; }
}

internal sealed class ReceiveDirectMaterialHandler : ICommandHandler<ReceiveDirectMaterial, PullResponse>
{
    public PullResponse Handle(ReceiveDirectMaterial command, IPlatform platform)
    {
        if (!DirectLabel.TryParse(command.Label, out var label))
        {
            return PullResponse.Fail(NvErrors.InvalidDirectLabel, $"Direct material label must have at least {DirectLabel.MinimumLength} characters and a valid quantity.");
        }

        var material = platform.ProjectionQuery<Material>().SingleOrDefault(m => m.NId == label.MaterialNId);
        if (material is null)
        {
            return PullResponse.Fail(NvErrors.UnknownMaterial, $"Material {label.MaterialNId} does not exist.");
        }

        if (command.Quantity <= 0)
        {
            return PullResponse.Fail(ProductErrors.InvalidQuantity, "Quantity must be greater than zero.");
        }

        var location = Inbound.Location(platform, command.StorageLocation);
        if (location is null)
        {
            return PullResponse.Fail(NvErrors.UnknownLocation, $"Storage location {command.StorageLocation} does not exist.");
        }

        var nid = material.NId;
        var factor = platform.ProjectionQuery<UomConversion>()
            .Where(u => u.MaterialNId == nid && u.System == UomSystems.Receiving && u.DestinationUom == material.UnitOfMeasure)
            .Select(u => (decimal?)u.Factor).SingleOrDefault() ?? 1m;
        var quantity = command.Quantity * factor;
        var shipped = label.ShipQuantity * factor;
        var prefix = label.Batch + "_";
        var received = platform.Query<Mtu>().Where(m => m.NId.StartsWith(prefix)).Select(m => m.Quantity).ToList().Sum();
        if (received + quantity > shipped)
        {
            return PullResponse.Fail(NvErrors.OverReceive, $"Cannot over receive the shipped quantity. Already received: {received / factor}.");
        }

        var lot = prefix + command.PalletNumber;
        var created = platform.CallCommand<CreateMtu, ProductResponse>(new CreateMtu { NId = lot, MaterialNId = nid, Quantity = quantity, EquipmentNId = location, Code = lot });
        if (!created.Succeeded)
        {
            return PullResponse.From(created);
        }

        var expiry = platform.Create<LotExpiry>();
        expiry.NId = lot;
        expiry.PoNo = label.PoNo;
        expiry.ExpirationDate = platform.Query<DirectDeliveryLine>().Where(l => l.Batch == label.Batch).Select(l => l.ExpirationDate).FirstOrDefault();
        platform.Submit(expiry);
        Pull.LinkReceived(platform, lot, location);
        return new PullResponse { NId = lot };
    }
}

/// <summary>Nạp lô vào buffer của khu đang chọn: một lô một buffer, nên lô còn ở buffer khác bị từ chối (lab L20). Khu không có buffer thì báo.</summary>
[Command<PullOperationBlock>]
public sealed class LoadLot : ICommand<PullResponse>
{
    [Mandatory]
    public string? EquipmentNId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class LoadLotHandler : ICommandHandler<LoadLot, PullResponse>
{
    public PullResponse Handle(LoadLot command, IPlatform platform)
    {
        var buffer = Pull.BufferOf(platform, command.EquipmentNId);
        if (buffer is null)
        {
            return PullResponse.Fail(NvErrors.NoBufferOnEquipment, $"{command.EquipmentNId} has no buffer: lot {command.MtuNId} could not be loaded.");
        }

        var linked = platform.CallCommand<LinkMtuToBuffer, ProductResponse>(new LinkMtuToBuffer { BufferNId = buffer, MtuNId = command.MtuNId });
        return linked.Succeeded ? new PullResponse { NId = command.MtuNId, Status = buffer } : PullResponse.From(linked);
    }
}

/// <summary>Gỡ lô khỏi buffer đang thuộc. Vị trí của lô giữ nguyên.</summary>
[Command<PullOperationBlock>]
public sealed class UnloadLot : ICommand<PullResponse>
{
    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class UnloadLotHandler : ICommandHandler<UnloadLot, PullResponse>
{
    public PullResponse Handle(UnloadLot command, IPlatform platform)
    {
        var detached = platform.CallCommand<DetachMtuFromBuffer, ProductResponse>(new DetachMtuFromBuffer { MtuNId = command.MtuNId });
        return detached.Succeeded ? new PullResponse { NId = command.MtuNId } : PullResponse.From(detached);
    }
}

public sealed class PullEquipmentParameters
{
    public string? EquipmentNId { get; init; }
}

public sealed class PullTaskParameters
{
    public string? Area { get; init; }

    /// <summary>Tier đích.</summary>
    public string? Level { get; init; }
}

public sealed class ScanParameters
{
    public string? TaskNId { get; init; }

    public string? Label { get; init; }
}

public sealed class LabelParameters
{
    public string? Label { get; init; }
}

public sealed class AndonDetailParameters
{
    public string? TaskNId { get; init; }
}

public sealed class NoPullParameters;

/// <summary>Một ô của màn gọi hàng.</summary>
public sealed class LinkedMaterialRow
{
    public string? MaterialNId { get; set; }

    public string? KanbanNo { get; set; }

    public string? RouteNId { get; set; }

    public string? PartAddressNId { get; set; }

    public string? BufferNId { get; set; }

    public decimal Quantity { get; set; }
}

/// <summary>
/// Ô gọi hàng của một khu: buffer ở ô có property <c>ProductionArea</c> = khu → material trong whitelist → hai nhánh: linh kiện có kanban và
/// part address còn hiệu lực, route có tier 4 là buffer đó; material đặt thẳng khai trên route, không kanban.
/// </summary>
[ReadingFunction(PullApp.Name)]
public sealed class LinkedMaterials : IReadingFunction<PullEquipmentParameters, LinkedMaterialRow>
{
    public IQueryable<LinkedMaterialRow> Read(PullEquipmentParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var cells = platform.ProjectionQuery<EquipmentProperty>()
            .Where(p => p.Name == "ProductionArea" && p.Value == parameters.EquipmentNId).Select(p => p.EquipmentNId);
        var buffers = platform.ProjectionQuery<Buffer>().Where(b => cells.Contains(b.EquipmentNId)).Select(b => b.NId).ToList();
        var routes = platform.ProjectionQuery<DeliveryRoute>().Where(r => buffers.Contains(r.Buffer4!)).ToList();
        var whitelist = platform.ProjectionQuery<BufferWhitelist>().Where(w => buffers.Contains(w.BufferNId!)).ToList();
        var routeNames = routes.Select(r => r.NId).ToList();
        var today = DateOnly.FromDateTime(platform.Time.GetUtcNow().UtcDateTime);
        var kanbans = platform.ProjectionQuery<Kanban>().ToList().Where(k => Kanbans.Active(k.EffectiveFrom, k.EffectiveTo, today)).ToList();
        var rows = new List<LinkedMaterialRow>();
        foreach (var address in platform.ProjectionQuery<PartAddress>().Where(a => routeNames.Contains(a.DeliveryRouteNId!)).ToList())
        {
            var route = routes.Single(r => r.NId == address.DeliveryRouteNId);
            var kanban = kanbans.FirstOrDefault(k => k.MaterialNId == address.MaterialNId && k.KanbanNo == address.KanbanNId);
            if (kanban is not null && Kanbans.Active(address.EffectiveFrom, address.EffectiveTo, today)
                && whitelist.Any(w => w.BufferNId == route.Buffer4 && w.MaterialNId == address.MaterialNId))
            {
                rows.Add(new LinkedMaterialRow { MaterialNId = address.MaterialNId, KanbanNo = kanban.KanbanNo, RouteNId = route.NId, PartAddressNId = address.NId, BufferNId = route.Buffer4, Quantity = kanban.QuantityPerContainer });
            }
        }

        foreach (var direct in platform.ProjectionQuery<RouteMaterial>().Where(r => routeNames.Contains(r.RouteNId!)).ToList())
        {
            var route = routes.Single(r => r.NId == direct.RouteNId);
            if (whitelist.Any(w => w.BufferNId == route.Buffer4 && w.MaterialNId == direct.MaterialNId))
            {
                rows.Add(new LinkedMaterialRow { MaterialNId = direct.MaterialNId, RouteNId = route.NId, BufferNId = route.Buffer4, Quantity = direct.Quantity });
            }
        }

        return rows.OrderBy(r => r.MaterialNId, StringComparer.Ordinal).AsQueryable();
    }
}

/// <summary>Một việc chở đang mở.</summary>
public sealed class PullTaskRow
{
    public string? TaskNId { get; set; }

    public string? RequestNId { get; set; }

    public string? RouteNId { get; set; }

    public string? Area { get; set; }

    public int Level { get; set; }

    public string? MaterialNId { get; set; }

    public decimal Quantity { get; set; }

    public string? KanbanNo { get; set; }

    public string? SourceBufferNId { get; set; }

    public string? DestinationBufferNId { get; set; }

    /// <summary>Trạng thái của việc chở; có lô rồi thì hiện <c>MaterialDelivered</c>.</summary>
    public string? Status { get; set; }

    public int? Priority { get; set; }

    public string? LeadTimeStatus { get; set; }

    public string? MtuNId { get; set; }

    public DateTimeOffset CreatedOn { get; set; }
}

internal static class PullRows
{
    public static List<PullTaskRow> Open(IReadingPlatform platform, Func<PullFlowProcess, DeliveryRoute, bool> filter)
    {
        var now = platform.Time.GetUtcNow();
        var tasks = platform.ProjectionQuery<TransportOperation>().Where(t => t.Status != TransportStatus.Complete && t.Status != TransportStatus.Cancelled).ToList();
        var requests = tasks.Select(t => t.LogisticRequestNId).ToList();
        var taskIds = tasks.Select(t => t.NId).ToList();
        var processes = platform.ProjectionQuery<PullFlowProcess>().Where(p => requests.Contains(p.NId)).ToDictionary(p => p.NId);
        var priorities = platform.ProjectionQuery<LogisticRequest>().Where(r => requests.Contains(r.NId)).ToDictionary(r => r.NId, r => r.Priority);
        var lots = platform.ProjectionQuery<PullTaskLot>().Where(l => taskIds.Contains(l.NId)).ToDictionary(l => l.NId, l => l.MtuNId);
        var routes = platform.ProjectionQuery<DeliveryRoute>().ToDictionary(r => r.NId);
        var addresses = platform.ProjectionQuery<PartAddress>().ToDictionary(a => a.NId, a => a.KanbanNId);
        var rows = new List<PullTaskRow>();
        foreach (var task in tasks)
        {
            if (!processes.TryGetValue(task.LogisticRequestNId!, out var process) || !routes.TryGetValue(process.RouteNId!, out var route) || !filter(process, route))
            {
                continue;
            }

            var lot = lots.GetValueOrDefault(task.NId);
            rows.Add(new PullTaskRow
            {
                TaskNId = task.NId,
                RequestNId = task.LogisticRequestNId,
                RouteNId = route.NId,
                Area = route.Area,
                Level = process.Level,
                MaterialNId = task.MaterialNId,
                Quantity = task.Quantity,
                KanbanNo = process.PartAddressNId is null ? null : addresses.GetValueOrDefault(process.PartAddressNId),
                SourceBufferNId = task.SourceBufferNId,
                DestinationBufferNId = task.DestinationBufferNId,
                Status = lot is null ? task.Status : "MaterialDelivered",
                Priority = priorities.GetValueOrDefault(task.LogisticRequestNId!),
                LeadTimeStatus = Pull.LeadTime(route, task.DestinationBufferNId, task.CreatedOn, now),
                MtuNId = lot,
                CreatedOn = task.CreatedOn,
            });
        }

        return [.. rows.OrderBy(r => r.CreatedOn)];
    }
}

/// <summary>Việc chở đang mở theo khu của route và tier đích.</summary>
[ReadingFunction(PullApp.Name)]
public sealed class PullTasks : IReadingFunction<PullTaskParameters, PullTaskRow>
{
    public IQueryable<PullTaskRow> Read(PullTaskParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        _ = int.TryParse(parameters.Level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level);
        return PullRows.Open(platform, (process, route) => route.Area == parameters.Area && process.Level == level).AsQueryable();
    }
}

/// <summary>Lệnh gọi đang mở của một khu sản xuất (màn gọi hàng).</summary>
[ReadingFunction(PullApp.Name)]
public sealed class AreaRequests : IReadingFunction<PullEquipmentParameters, PullTaskRow>
{
    public IQueryable<PullTaskRow> Read(PullEquipmentParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var cells = platform.ProjectionQuery<EquipmentProperty>()
            .Where(p => p.Name == "ProductionArea" && p.Value == parameters.EquipmentNId).Select(p => p.EquipmentNId);
        var buffers = platform.ProjectionQuery<Buffer>().Where(b => cells.Contains(b.EquipmentNId)).Select(b => b.NId).ToList();
        return PullRows.Open(platform, (process, route) => process.Level == 4 && buffers.Contains(route.Buffer4!)).AsQueryable();
    }
}

/// <summary>Một lô quét được cho một việc chở.</summary>
public sealed class ScannedLotRow
{
    public string? MtuNId { get; set; }

    public string? MaterialNId { get; set; }

    public decimal Quantity { get; set; }

    public string? CurrentBufferNId { get; set; }

    public string? ExpirationDate { get; set; }

    public bool IsInSourceBuffer { get; set; }

    public bool IsFefo { get; set; }

    public string? FefoMtuNId { get; set; }
}

/// <summary>Quét nhãn cho một việc chở: chỉ ra lô đang thuộc một buffer; lô chưa gắn buffer thì coi như không tìm thấy.</summary>
[ReadingFunction(PullApp.Name)]
public sealed class ScanLot : IReadingFunction<ScanParameters, ScannedLotRow>
{
    public IQueryable<ScannedLotRow> Read(ScanParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var task = platform.ProjectionQuery<TransportOperation>().SingleOrDefault(t => t.NId == parameters.TaskNId);
        if (task is null)
        {
            return Enumerable.Empty<ScannedLotRow>().AsQueryable();
        }

        var process = platform.ProjectionQuery<PullFlowProcess>().SingleOrDefault(p => p.NId == task.LogisticRequestNId);
        var route = process is null ? null : platform.ProjectionQuery<DeliveryRoute>().SingleOrDefault(r => r.NId == process.RouteNId);
        var first = Fefo.First(platform, task, route);
        var mtus = Pull.Search(platform, parameters.Label).ToList();
        var names = mtus.Select(m => m.NId).ToList();
        var buffers = platform.ProjectionQuery<BufferAssignment>().Where(a => names.Contains(a.NId)).ToDictionary(a => a.NId, a => a.BufferNId);
        var dates = platform.ProjectionQuery<LotExpiry>().Where(e => names.Contains(e.NId)).ToDictionary(e => e.NId, e => e.ExpirationDate);
        return mtus.Where(m => buffers.ContainsKey(m.NId)).Select(m => new ScannedLotRow
        {
            MtuNId = m.NId,
            MaterialNId = m.MaterialNId,
            Quantity = m.Quantity,
            CurrentBufferNId = buffers[m.NId],
            ExpirationDate = dates.GetValueOrDefault(m.NId),
            IsInSourceBuffer = buffers[m.NId] == task.SourceBufferNId,
            IsFefo = first == m.NId,
            FefoMtuNId = first,
        }).ToList().AsQueryable();
    }
}

/// <summary>Một dòng của bảng gọi hàng.</summary>
public sealed class AndonRow
{
    public string? TaskNId { get; set; }

    public string? RouteNId { get; set; }

    public string? Area { get; set; }

    public string? MaterialNId { get; set; }

    public string? KanbanNo { get; set; }

    public int? Priority { get; set; }

    /// <summary>Tổng phút các chặng × 60.</summary>
    public int StandardSeconds { get; set; }

    public DateTimeOffset CalledOn { get; set; }

    /// <summary>Trống khi dòng thường nằm chung với một dòng gấp đang chọn.</summary>
    public string? CallStatus { get; set; }

    public string? TaskStatus { get; set; }
}

/// <summary>Trạng thái của bảng gọi hàng.</summary>
public static class AndonStatus
{
    public const string HotCall = "HotCall";
    public const string Behind = "Behind";
    public const string OnTime = "OnTime";
    public const string GettingCritical = "GettingCritical";
    public const string Pending = "Pending";

    /// <summary>
    /// Thứ tự xét giữ như hệ thật: gấp, trễ, rồi đúng hạn với điều kiện rộng. Hai nhánh sau không tới được, vì chưa trễ nghĩa là chuẩn ≥ đã qua,
    /// tức luôn ≥ 0,2 lần đã qua. Hệ quả: lệnh còn Released và chưa trễ vẫn là đúng hạn, không phải chờ.
    /// </summary>
    public static string? Of(bool selectedIsHot, double elapsedSeconds, int standardSeconds, string? taskStatus)
    {
        if (selectedIsHot)
        {
            return HotCall;
        }

        if (elapsedSeconds > standardSeconds)
        {
            return Behind;
        }

        if (standardSeconds >= 0.2 * elapsedSeconds)
        {
            return OnTime;
        }

        if (standardSeconds >= 0.8 * elapsedSeconds)
        {
            return GettingCritical;
        }

        return taskStatus == TransportStatus.Released ? Pending : null;
    }
}

internal static class Andon
{
    /// <summary>Chỉ lệnh có part address và kanban mới lên bảng; lệnh đặt thẳng không bao giờ lên.</summary>
    public static List<AndonRow> Rows(IReadingPlatform platform, Func<DeliveryRoute, bool>? filter = null)
    {
        var routes = platform.ProjectionQuery<DeliveryRoute>().ToDictionary(r => r.NId);
        var open = PullRows.Open(platform, (_, route) => filter is null || filter(route));
        var tasks = platform.ProjectionQuery<TransportOperation>().ToDictionary(t => t.NId, t => t.Status);
        return [.. open.Where(r => r.KanbanNo is not null).Select(r =>
        {
            var route = routes[r.RouteNId!];
            return new AndonRow
            {
                TaskNId = r.TaskNId,
                RouteNId = r.RouteNId,
                Area = r.Area,
                MaterialNId = r.MaterialNId,
                KanbanNo = r.KanbanNo,
                Priority = r.Priority,
                StandardSeconds = (route.Minutes1 + route.Minutes2 + route.Minutes3) * 60,
                CalledOn = r.CreatedOn,
                TaskStatus = tasks[r.TaskNId!],
            };
        })];
    }
}

/// <summary>Trang đầu bảng gọi hàng: một dòng mỗi lệnh gọi linh kiện đang mở, không gộp theo route.</summary>
[ReadingFunction(PullApp.Name)]
public sealed class AndonRoutes : IReadingFunction<NoPullParameters, AndonRow>
{
    public IQueryable<AndonRow> Read(NoPullParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return Andon.Rows(platform).AsQueryable();
    }
}

/// <summary>
/// Trang chi tiết mở từ một dòng: mọi lệnh cùng route và khu. Trạng thái mọi dòng tính theo priority và giờ gọi của dòng đang chọn, không
/// của từng dòng, như hệ thật: chọn dòng gấp thì dòng thường hiện trống và không dòng nào trễ.
/// </summary>
[ReadingFunction(PullApp.Name)]
public sealed class AndonDetail : IReadingFunction<AndonDetailParameters, AndonRow>
{
    public IQueryable<AndonRow> Read(AndonDetailParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var all = Andon.Rows(platform);
        var selected = all.SingleOrDefault(r => r.TaskNId == parameters.TaskNId);
        if (selected is null)
        {
            return Enumerable.Empty<AndonRow>().AsQueryable();
        }

        var elapsed = (platform.Time.GetUtcNow() - selected.CalledOn).TotalSeconds;
        var rows = all.Where(r => r.RouteNId == selected.RouteNId && r.Area == selected.Area).ToList();
        foreach (var row in rows)
        {
            var status = AndonStatus.Of(selected.Priority == 1, elapsed, row.StandardSeconds, row.TaskStatus);
            row.CallStatus = row.Priority == 1 ? AndonStatus.HotCall : status == AndonStatus.HotCall ? null : status;
        }

        return rows.AsQueryable();
    }
}

/// <summary>Lô đang thuộc buffer của khu đang chọn.</summary>
[ReadingFunction(PullApp.Name)]
public sealed class LoadedLots : IReadingFunction<PullEquipmentParameters, Mtu>
{
    public IQueryable<Mtu> Read(PullEquipmentParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var buffers = platform.ProjectionQuery<Buffer>().Where(b => b.EquipmentNId == parameters.EquipmentNId).Select(b => b.NId);
        var lots = platform.ProjectionQuery<BufferAssignment>().Where(a => buffers.Contains(a.BufferNId)).Select(a => a.MtuNId);
        return platform.ProjectionQuery<Mtu>().Where(m => lots.Contains(m.NId));
    }
}

/// <summary>Tìm lô theo nhãn; không lọc theo khu hay buffer.</summary>
[ReadingFunction(PullApp.Name)]
public sealed class SearchLots : IReadingFunction<LabelParameters, Mtu>
{
    public IQueryable<Mtu> Read(LabelParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        return Pull.Search(platform, parameters.Label);
    }
}
