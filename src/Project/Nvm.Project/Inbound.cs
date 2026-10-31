using System.Globalization;
using System.Text;
using System.Text.Json;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Xe linh kiện (R9): lịch xe từ SupplierPortal, đăng ký ở cổng, placard, gọi xe vào dock, nhận skid, chất hàng trả, yard pass, xe rời;
// xe rỗng, xe không lịch, chuyển skid giữa hai xe. Lịch nói về chuyến, trailer nói về chiếc xe: mỗi bước đổi trạng thái của cả hai.

public static class TrailerContents
{
    public const string Component = "Component";
    public const string ComponentReturn = "ComponentReturn";
    public const string DirectMaterial = "DirectMaterial";
    public const string PipedDirectMaterial = "PipedDirectMaterial";
    public const string AdHocComponents = "AdHocComponents";
    public const string ReturnedGoods = "ReturnedGoods";
}

/// <summary>Năm loại xe tới không có lịch.</summary>
public static class UnplannedTypes
{
    public const string DirectMaterial = "DirectMaterial";
    public const string PipedDirectMaterial = "PipedDirectMaterial";
    public const string AdHocComponents = "AdHocComponents";
    public const string CustomerEmpty = "CustomerEmpty";
    public const string ReturnedGoods = "ReturnedGoods";

    /// <summary>Xe của hãng vận chuyển: rời đi không cần yard pass, thành Deleted thay vì Free.</summary>
    public static readonly IReadOnlyList<string> CarrierOwned = [DirectMaterial, PipedDirectMaterial, AdHocComponents];
}

public static class ScheduleTypes
{
    public const string Unloading = "Unloading";
    public const string Loading = "Loading";
}

public static class GateStates
{
    public const string Planned = "Planned";
    public const string InTransit = "InTransit";
    public const string Created = "Created";
    public const string Arrived = "Arrived";
    public const string ForDeparture = "ForDeparture";
    public const string Completed = "Completed";
    public const string CompletedReloaded = "CompletedReloaded";
    public const string Departed = "Departed";

    public static readonly IReadOnlyList<string> Closed = [Completed, CompletedReloaded, Departed];
}

public static class LoadingStates
{
    public const string ToArrive = "ToArriveAtYard";
    public const string ToBeUnloaded = "ToBeUnloaded";
    public const string Unloading = "Unloading";
    public const string Unloaded = "Unloaded";
    public const string ToBeLoaded = "ToBeLoaded";
    public const string Loaded = "Loaded";
}

public static class PlacardTypes
{
    public const string Component = "Component";
    public const string Empty = "Empty";
    public const string ReturnComponent = "ReturnComponent";
    public const string ReturnGoods = "ReturnGoods";

    public static string Prefix(string? type) => type switch
    {
        Component => "PL-C",
        Empty => "PL-E",
        ReturnComponent => "PL-R",
        ReturnGoods => "PL-G",
        _ => string.Empty,
    };
}

/// <summary>Một chuyến xe của một route: dỡ hoặc chất; một trailer có thể mang nhiều lịch cùng lúc sau khi nhận skid chuyển sang.</summary>
[Entity<YardOperationBlock>]
public sealed class InboundSchedule : Entity
{
    public string? Route { get; set; }

    /// <summary>Số xe nhà cung cấp báo trước; rỗng thì lịch ở trạng thái Planned.</summary>
    public string? TrailerNo { get; set; }

    public string? TrailerNId { get; set; }

    public string? ScheduleType { get; set; }

    public string? MaterialType { get; set; }

    public string? GateStatus { get; set; }

    public string? LoadingStatus { get; set; }

    public string? PlacardNId { get; set; }

    public int PlannedSkids { get; set; }

    public decimal PercentReloaded { get; set; }

    public bool WithTransferredSkids { get; set; }

    public bool Unplanned { get; set; }

    public DateTimeOffset? PlannedOn { get; set; }

    public DateTimeOffset? ArrivedOn { get; set; }

    public DateTimeOffset? DepartedOn { get; set; }
}

/// <summary>Skid nhà cung cấp báo đã chất lên xe. Nhận xong thành đúng một MTU.</summary>
[Entity<YardOperationBlock>]
public sealed class InboundSkid : Entity
{
    [Mandatory]
    public string? ScheduleNId { get; set; }

    /// <summary>Route gốc; không đổi khi skid chuyển sang xe khác, nhãn cũng không đổi.</summary>
    public string? Route { get; set; }

    public string? PoNo { get; set; }

    public string? SupplierCode { get; set; }

    public string? DockCode { get; set; }

    public string? PalletCode { get; set; }

    public string? SkidNo { get; set; }

    public string? MaterialNId { get; set; }

    public string? KanbanNId { get; set; }

    public decimal QuantityPerBox { get; set; }

    public decimal BoxCount { get; set; }

    public bool Verified { get; set; }

    public string? MtuNId { get; set; }

    public DateTimeOffset? ReceivedOn { get; set; }
}

/// <summary>Lịch sử đổi xe của skid: lần chuyển đầu thêm một dòng Arrival (xe chở đầu tiên), mỗi lần chuyển một dòng Transload.</summary>
[Entity<YardOperationBlock>]
public sealed class SkidTransload : Entity
{
    [Mandatory]
    public string? SkidNId { get; set; }

    public string? Kind { get; set; }

    public string? FromTrailerNId { get; set; }

    public string? ToTrailerNId { get; set; }

    public string? ReasonNId { get; set; }

    public string? OldRoute { get; set; }
}

[Entity<YardOperationBlock>]
public sealed class TrailerPlacard : Entity
{
    public string? Type { get; set; }

    public string? Route { get; set; }

    public string? TrailerNId { get; set; }
}

/// <summary>Danh mục lý do chuyển skid; NId là tên lý do.</summary>
[Entity<YardOperationBlock>]
public sealed class TransferReason : Entity;

/// <summary>Nhà cung cấp tại chỗ (NId = mã nhà cung cấp): lịch của họ không qua đăng ký xe thường.</summary>
[Entity<YardOperationBlock>]
public sealed class LocalSupplier : Entity;

/// <summary>Material được tự nhận khi có đơn. NId = <c>mã nhà cung cấp|material</c>.</summary>
[Entity<YardOperationBlock>]
public sealed class LocalSupplierMaterial : Entity
{
    public string? SupplierCode { get; set; }

    public string? MaterialNId { get; set; }
}

/// <summary>Khu bãi gán cho người kéo xe. NId = <c>user|khu</c>; người không có dòng nào thấy mọi yêu cầu.</summary>
[Entity<YardOperationBlock>]
public sealed class ShuntZone : Entity
{
    public string? UserNId { get; set; }

    public string? AreaNId { get; set; }
}

/// <summary>Nhãn skid: độ dài cố định, cắt theo vị trí. Route không đem ra so nên nhãn vẫn dùng được sau khi skid đổi xe.</summary>
public sealed record SkidLabel(string Site, string Supplier, string Dock, string PoNo, string Route, string Pallet, string Skid)
{
    public const int Length = 40;

    public static bool TryParse(string? barcode, out SkidLabel label)
    {
        label = null!;
        if (barcode is null || barcode.Length != Length)
        {
            return false;
        }

        label = new SkidLabel(barcode[..4], barcode[4..9], barcode[9..11], barcode[11..21].TrimEnd(), barcode[21..29].TrimEnd(),
            barcode[29..31].TrimEnd(), barcode[31..34]);
        return true;
    }

    /// <summary>Dựng nhãn như nhà cung cấp in; PO dài hơn ô bị cắt, nên về sau không khớp với đơn (lab L19).</summary>
    public static string Build(string supplier, string dock, string po, string route, string pallet, string skid)
    {
        static string Cell(string value, int width) => value.PadRight(width)[..width];
        return "NV01" + Cell(supplier, 5) + Cell(dock, 2) + Cell(po, 10) + Cell(route, 8) + Cell(pallet, 2) + Cell(skid, 3) + "X" + "SPARE";
    }
}

internal static class Inbound
{
    public static List<InboundSchedule> OpenSchedules(IPlatform platform, string trailer) =>
        [.. platform.Query<InboundSchedule>().Where(s => s.TrailerNId == trailer && !GateStates.Closed.Contains(s.GateStatus!))];

    public static string NewPlacard(IPlatform platform, string type, string? route, string? trailer)
    {
        var placard = platform.Create<TrailerPlacard>();
        placard.NId = PlacardTypes.Prefix(type) + Counters.Base36(Counters.Take(platform, "Placard" + type) + 1).PadLeft(6, '0');
        placard.Type = type;
        placard.Route = route;
        placard.TrailerNId = trailer;
        platform.Submit(placard);
        return placard.NId;
    }

    /// <summary>Gắn placard vào xe: placard đã phát cho xe khác thì không gắn được.</summary>
    public static YardResponse? Attach(IPlatform platform, string? placardNId, string type, string? route, Trailer trailer)
    {
        var placard = platform.Query<TrailerPlacard>().SingleOrDefault(p => p.NId == placardNId);
        if (placard is null || placard.Type != type)
        {
            return YardResponse.Fail(NvErrors.InvalidPlacard, $"Placard {placardNId} does not exist or is not a {type} placard.");
        }

        if (!string.IsNullOrEmpty(route) && placard.Route != route)
        {
            return YardResponse.Fail(NvErrors.InvalidPlacard, $"Placard {placardNId} is for route {placard.Route}, not {route}.");
        }

        if (!string.IsNullOrEmpty(placard.TrailerNId) && placard.TrailerNId != trailer.NId)
        {
            return YardResponse.Fail(NvErrors.InvalidPlacard, $"Placard {placardNId} is on trailer {placard.TrailerNId}.");
        }

        placard.TrailerNId = trailer.NId;
        platform.Submit(placard);
        trailer.PlacardNId = placard.NId;
        return null;
    }

    public static void ReleasePlacard(IPlatform platform, Trailer trailer)
    {
        if (platform.Query<TrailerPlacard>().SingleOrDefault(p => p.NId == trailer.PlacardNId) is { } placard)
        {
            placard.TrailerNId = null;
            platform.Submit(placard);
        }

        trailer.PlacardNId = null;
    }

    /// <summary>Route trên xe: ghép route các lịch đang mở, sắp chữ cái.</summary>
    public static void RefreshRoutes(IPlatform platform, Trailer trailer, IEnumerable<InboundSchedule>? extra = null)
    {
        var routes = OpenSchedules(platform, trailer.NId).Concat(extra ?? []).Select(s => s.Route)
            .Where(r => !string.IsNullOrEmpty(r)).Distinct().Order(StringComparer.Ordinal);
        trailer.Route = string.Join(',', routes);
    }

    public static InboundSchedule NewSchedule(IPlatform platform, string key, string? route, Trailer? trailer, string type, string material, string gate, string loading)
    {
        var schedule = platform.Create<InboundSchedule>();
        schedule.NId = key;
        schedule.Route = route;
        schedule.TrailerNId = trailer?.NId;
        schedule.TrailerNo = trailer?.TrailerNo;
        schedule.ScheduleType = type;
        schedule.MaterialType = material;
        schedule.GateStatus = gate;
        schedule.LoadingStatus = loading;
        schedule.PlannedOn = platform.Time.GetUtcNow();
        return schedule;
    }

    public static string Key(IPlatform platform, string prefix) => prefix + Counters.Base36(Counters.Take(platform, "Schedule" + prefix) + 1).PadLeft(8, '0');

    /// <summary>Vị trí quét: mã phụ (property <c>ScanCode</c>) trước, rồi tên equipment.</summary>
    public static string? Location(IPlatform platform, string? scanned)
    {
        var byCode = platform.ProjectionQuery<EquipmentProperty>().Where(p => p.Name == "ScanCode" && p.Value == scanned).Select(p => p.EquipmentNId).FirstOrDefault();
        return byCode ?? platform.ProjectionQuery<Equipment>().Where(e => e.NId == scanned).Select(e => e.NId).SingleOrDefault();
    }

    public static string? Property(IReadOnlyPlatform platform, string? equipment, string name)
    {
        var nid = equipment + "|" + name;
        return platform.ProjectionQuery<EquipmentProperty>().Where(p => p.NId == nid).Select(p => p.Value).SingleOrDefault();
    }

    public static string? DefaultStore(IPlatform platform, InboundSkid skid) =>
        platform.ProjectionQuery<PartAddress>().Where(a => a.MaterialNId == skid.MaterialNId && a.KanbanNId == skid.KanbanNId)
            .OrderBy(a => a.StoreAddress).Select(a => a.StoreAddress).FirstOrDefault();

    /// <summary>Skid thành MTU, tên ghép từ nhãn; số lượng = số chiếc mỗi hộp × số hộp.</summary>
    public static YardResponse? Receive(IPlatform platform, InboundSkid skid, string location)
    {
        var nid = skid.SupplierCode + skid.DockCode + skid.PoNo + skid.PalletCode + skid.SkidNo;
        var mtu = platform.CallCommand<CreateMtu, ProductResponse>(new CreateMtu
        {
            NId = nid,
            MaterialNId = skid.MaterialNId,
            Quantity = skid.QuantityPerBox * skid.BoxCount,
            EquipmentNId = location,
        });
        if (!mtu.Succeeded)
        {
            return YardResponse.From(mtu);
        }

        skid.Verified = true;
        skid.MtuNId = nid;
        skid.ReceivedOn = platform.Time.GetUtcNow();
        platform.Submit(skid);
        return null;
    }

    public static InboundSkid? Match(IPlatform platform, IEnumerable<string> schedules, SkidLabel label)
    {
        var ids = schedules.ToList();
        return platform.Query<InboundSkid>().FirstOrDefault(s => ids.Contains(s.ScheduleNId!) && s.SupplierCode == label.Supplier && s.DockCode == label.Dock
            && s.PoNo == label.PoNo && s.PalletCode == label.Pallet && s.SkidNo == label.Skid);
    }

    public static void Comment(Trailer trailer, string text) => trailer.LastComment = text;
}

/// <summary>Lịch xe từ cổng nhà cung cấp, một chuyến một tin. Hàng của nhà cung cấp tại chỗ thuộc danh sách tự nhận được nhận ngay.</summary>
[Command<YardOperationBlock>]
public sealed class ReceiveDeliverySchedule : ICommand<YardResponse>
{
    [Mandatory]
    public string? Payload { get; init; }
}

internal sealed class ReceiveDeliveryScheduleHandler : ICommandHandler<ReceiveDeliverySchedule, YardResponse>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public YardResponse Handle(ReceiveDeliverySchedule command, IPlatform platform)
    {
        DeliveryPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DeliveryPayload>(command.Payload!, Json);
        }
        catch (JsonException e)
        {
            return YardResponse.Fail(NvErrors.InvalidDelivery, "Delivery payload is not valid JSON: " + e.Message);
        }

        if (payload is null || string.IsNullOrEmpty(payload.Route) || payload.Skids is null || payload.Skids.Count == 0)
        {
            return YardResponse.Fail(NvErrors.InvalidDelivery, "Delivery payload needs a route and at least one skid.");
        }

        var key = payload.Route + "-" + payload.PlannedOn.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        if (platform.Query<InboundSchedule>().Any(s => s.NId == key))
        {
            return YardResponse.Fail(NvErrors.InvalidDelivery, $"Route {payload.Route} already has a delivery at {payload.PlannedOn:u}.");
        }

        var schedule = platform.Create<InboundSchedule>();
        schedule.NId = key;
        schedule.Route = payload.Route;
        schedule.TrailerNo = payload.TrailerNo;
        schedule.ScheduleType = ScheduleTypes.Unloading;
        schedule.MaterialType = TrailerContents.Component;
        schedule.GateStatus = string.IsNullOrEmpty(payload.TrailerNo) ? GateStates.Planned : GateStates.InTransit;
        schedule.LoadingStatus = LoadingStates.ToArrive;
        schedule.PlannedOn = payload.PlannedOn;
        schedule.PlannedSkids = payload.PlannedSkids ?? payload.Skids.Count;
        platform.Submit(schedule);

        var auto = platform.Query<LocalSupplierMaterial>().Select(m => m.NId).ToHashSet();
        var skids = new List<InboundSkid>();
        foreach (var line in payload.Skids)
        {
            var skid = platform.Create<InboundSkid>();
            skid.NId = skid.Id.ToString("N");
            skid.ScheduleNId = key;
            skid.Route = payload.Route;
            skid.PoNo = line.PoNo;
            skid.SupplierCode = line.Supplier;
            skid.DockCode = line.Dock;
            skid.PalletCode = line.Pallet;
            skid.SkidNo = line.Skid;
            skid.MaterialNId = line.Material;
            skid.KanbanNId = line.Kanban;
            skid.QuantityPerBox = line.QuantityPerBox;
            skid.BoxCount = line.BoxCount;
            platform.Submit(skid);
            skids.Add(skid);

            if (auto.Contains(line.Supplier + "|" + line.Material) && Inbound.DefaultStore(platform, skid) is { } store
                && Inbound.Receive(platform, skid, store) is { } failed)
            {
                return failed;
            }
        }

        if (skids.All(s => s.Verified))
        {
            schedule.LoadingStatus = LoadingStates.Unloaded;
            schedule.GateStatus = GateStates.Completed;
            platform.Submit(schedule);
        }

        return new YardResponse { NId = key, Status = schedule.GateStatus };
    }

    private sealed record DeliveryPayload(string? Route, string? TrailerNo, DateTimeOffset PlannedOn, int? PlannedSkids, List<SkidLine>? Skids);

    private sealed record SkidLine(string? PoNo, string? Supplier, string? Dock, string? Pallet, string? Skid, string? Material, string? Kanban, decimal QuantityPerBox, decimal BoxCount);
}

/// <summary>Xe linh kiện tới cổng: quét barcode, chọn ô yard, cấp placard linh kiện. Lịch Arrived chờ dỡ; xe Full, loại Component.</summary>
[Command<YardOperationBlock>]
public sealed class ArriveComponentTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? ScheduleNId { get; init; }

    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? LocationNId { get; init; }
}

internal sealed class ArriveComponentTrailerHandler : ICommandHandler<ArriveComponentTrailer, YardResponse>
{
    public YardResponse Handle(ArriveComponentTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        var schedule = platform.Query<InboundSchedule>().SingleOrDefault(s => s.NId == command.ScheduleNId);
        if (schedule is null || schedule.GateStatus is not (GateStates.Planned or GateStates.InTransit))
        {
            return YardResponse.Fail(NvErrors.InvalidDelivery, $"Schedule {command.ScheduleNId} does not exist or has already arrived.");
        }

        var suppliers = platform.Query<InboundSkid>().Where(s => s.ScheduleNId == schedule.NId).Select(s => s.SupplierCode).Distinct().ToList();
        var local = platform.Query<LocalSupplier>().Select(s => s.NId).ToHashSet();
        if (suppliers.Count > 0 && suppliers.All(s => local.Contains(s!)))
        {
            return YardResponse.Fail(NvErrors.LocalSupplierRoute, $"Schedule {schedule.NId} only carries local suppliers; it does not register at the gate.");
        }

        if (!string.IsNullOrEmpty(schedule.TrailerNo) && schedule.TrailerNo != trailer.TrailerNo)
        {
            return YardResponse.Fail(NvErrors.WrongTrailer, $"Trailer {trailer.TrailerNo} scanned; schedule {schedule.NId} expects {schedule.TrailerNo}.");
        }

        if (!string.IsNullOrEmpty(trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is in the plant at {trailer.LocationNId}.");
        }

        if (trailer.Status != TrailerStates.Free)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status}; only a free trailer registers.");
        }

        if (Inbound.Property(platform, command.LocationNId, "LocationType") != "Yard")
        {
            return YardResponse.Fail(NvErrors.UnknownLocation, $"{command.LocationNId} is not a yard slot.");
        }

        if (platform.Query<Trailer>().Any(t => t.LocationNId == command.LocationNId))
        {
            return YardResponse.Fail(NvErrors.LocationBusy, $"Location {command.LocationNId} has a trailer already.");
        }

        var placard = Inbound.NewPlacard(platform, PlacardTypes.Component, schedule.Route, trailer.NId);
        schedule.TrailerNId = trailer.NId;
        schedule.TrailerNo = trailer.TrailerNo;
        schedule.GateStatus = GateStates.Arrived;
        schedule.LoadingStatus = LoadingStates.ToBeUnloaded;
        schedule.PlacardNId = placard;
        schedule.ArrivedOn = platform.Time.GetUtcNow();
        platform.Submit(schedule);

        trailer.LocationNId = command.LocationNId;
        trailer.ContentType = TrailerContents.Component;
        trailer.PlacardNId = placard;
        trailer.Route = schedule.Route;
        Yard.SetStatus(platform, trailer, TrailerStates.Full);
        platform.Submit(trailer);
        return new YardResponse { NId = placard, Status = trailer.Status };
    }
}

/// <summary>Xe tới không có lịch. Xe của hãng vận chuyển tạo trailer theo biển số nếu chưa có, vào thẳng vị trí chọn, không qua yard.</summary>
[Command<YardOperationBlock>]
public sealed class RegisterUnplannedTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? TrailerType { get; init; }

    /// <summary>Biển số (xe của hãng vận chuyển) hoặc barcode của trailer đã khai báo (hai loại còn lại).</summary>
    [Mandatory]
    public string? Trailer { get; init; }

    public string? Carrier { get; init; }

    public string? LocationNId { get; init; }

    public string? Route { get; init; }

    public string? PlacardNId { get; init; }

    public string? Comments { get; init; }
}

internal sealed class RegisterUnplannedTrailerHandler : ICommandHandler<RegisterUnplannedTrailer, YardResponse>
{
    public YardResponse Handle(RegisterUnplannedTrailer command, IPlatform platform)
    {
        var type = command.TrailerType;
        var carrierOwned = UnplannedTypes.CarrierOwned.Contains(type);
        if (!carrierOwned && type is not (UnplannedTypes.CustomerEmpty or UnplannedTypes.ReturnedGoods))
        {
            return YardResponse.Fail(NvErrors.InvalidUnplannedType, $"Trailer type {type} is not known.");
        }

        if (carrierOwned && string.IsNullOrEmpty(command.Carrier))
        {
            return YardResponse.Fail(NvErrors.InvalidUnplannedType, $"Trailer type {type} needs a carrier.");
        }

        var trailer = platform.Query<Trailer>().SingleOrDefault(t => t.NId == command.Trailer);
        if (trailer is null)
        {
            if (!carrierOwned)
            {
                return YardResponse.Fail(NvErrors.UnknownTrailer, $"Trailer {command.Trailer} does not exist; type {type} takes a declared trailer.");
            }

            trailer = platform.Create<Trailer>();
            trailer.NId = command.Trailer!;
            trailer.TrailerNo = command.Trailer;
            platform.ChangeStatus(trailer, TrailerStates.Machine, TrailerStates.Free);
        }

        if (!string.IsNullOrEmpty(trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is in the plant at {trailer.LocationNId}.");
        }

        if (trailer.Status is not (TrailerStates.Free or TrailerStates.Deleted))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status}.");
        }

        if (type != UnplannedTypes.PipedDirectMaterial && Check(platform, type!, command.LocationNId) is { } refused)
        {
            return refused;
        }

        var (status, content) = type switch
        {
            UnplannedTypes.CustomerEmpty => (TrailerStates.CustomerEmpty, null),
            UnplannedTypes.ReturnedGoods => (TrailerStates.FullReturned, TrailerContents.ReturnedGoods),
            _ => (TrailerStates.Full, type),
        };

        if (type == UnplannedTypes.CustomerEmpty && Inbound.Attach(platform, command.PlacardNId, PlacardTypes.Empty, null, trailer) is { } noEmpty)
        {
            return noEmpty;
        }

        if (type == UnplannedTypes.ReturnedGoods)
        {
            if (string.IsNullOrEmpty(command.Route))
            {
                return YardResponse.Fail(NvErrors.InvalidUnplannedType, "Returned goods need a route.");
            }

            if (Inbound.Attach(platform, command.PlacardNId, PlacardTypes.ReturnGoods, command.Route, trailer) is { } noReturn)
            {
                return noReturn;
            }
        }

        var schedule = Inbound.NewSchedule(platform, Inbound.Key(platform, "UNS-"), command.Route, trailer, ScheduleTypes.Unloading, type!, GateStates.Arrived, LoadingStates.ToBeUnloaded);
        schedule.Unplanned = true;
        schedule.ArrivedOn = platform.Time.GetUtcNow();
        schedule.PlacardNId = trailer.PlacardNId;
        platform.Submit(schedule);

        trailer.Carrier = command.Carrier;
        trailer.ContentType = content;
        trailer.Route = command.Route;
        trailer.LocationNId = type == UnplannedTypes.PipedDirectMaterial ? null : command.LocationNId;
        trailer.LastComment = command.Comments;
        Yard.SetStatus(platform, trailer, status);
        platform.Submit(trailer);
        return new YardResponse { NId = trailer.NId, Status = trailer.Status };
    }

    private static YardResponse? Check(IPlatform platform, string type, string? location)
    {
        if (string.IsNullOrEmpty(location) || !platform.ProjectionQuery<Equipment>().Any(e => e.NId == location))
        {
            return YardResponse.Fail(NvErrors.UnknownLocation, $"Location {location} does not exist.");
        }

        if (platform.Query<Trailer>().Any(t => t.LocationNId == location) || platform.Query<TrailerMovement>().Any(m => m.ToNId == location && m.Status != MovementStates.Dropped))
        {
            return YardResponse.Fail(NvErrors.LocationBusy, $"Location {location} has a trailer or a request already.");
        }

        var group = Inbound.Property(platform, location, "ArrivalGroup");
        var allowed = type switch
        {
            UnplannedTypes.DirectMaterial => group == "DirectArea",
            UnplannedTypes.AdHocComponents => group == "AdHocArea",
            UnplannedTypes.CustomerEmpty => group == "EmptyArea",
            _ => group != "EmptyArea",
        };
        return allowed ? null : YardResponse.Fail(NvErrors.UnknownLocation, $"Location {location} does not take a trailer of type {type}.");
    }
}

/// <summary>Sinh placard để in sẵn (xe rỗng, hàng trả). Placard linh kiện cấp lúc đăng ký nên không sinh ở đây.</summary>
[Command<YardOperationBlock>]
public sealed class GeneratePlacard : ICommand<YardResponse>
{
    [Mandatory]
    public string? Type { get; init; }

    public string? Route { get; init; }
}

internal sealed class GeneratePlacardHandler : ICommandHandler<GeneratePlacard, YardResponse>
{
    public YardResponse Handle(GeneratePlacard command, IPlatform platform)
    {
        if (command.Type is not (PlacardTypes.Empty or PlacardTypes.ReturnComponent or PlacardTypes.ReturnGoods))
        {
            return YardResponse.Fail(NvErrors.InvalidPlacard, $"Placard type {command.Type} is not generated here.");
        }

        if (command.Type != PlacardTypes.Empty && string.IsNullOrEmpty(command.Route))
        {
            return YardResponse.Fail(NvErrors.InvalidPlacard, $"A {command.Type} placard needs a route.");
        }

        return new YardResponse { NId = Inbound.NewPlacard(platform, command.Type, command.Route, null) };
    }
}

/// <summary>Gọi xe rỗng vào dock để chất hàng: tạo lịch chất hàng và yêu cầu kéo xe.</summary>
[Command<YardOperationBlock>]
public sealed class CallEmptyTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? DockNId { get; init; }

    public string? Priority { get; init; }
}

internal sealed class CallEmptyTrailerHandler : ICommandHandler<CallEmptyTrailer, YardResponse>
{
    public YardResponse Handle(CallEmptyTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (trailer.Status != TrailerStates.Empty || !string.IsNullOrEmpty(trailer.StorageMtuaNId))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status}; only an empty non-storage trailer is called.");
        }

        if (!Yard.IsDock(platform, command.DockNId))
        {
            return YardResponse.Fail(NvErrors.TrailerNotAtDock, $"{command.DockNId} is not a dock door.");
        }

        var moved = Yard.Move(platform, trailer, command.DockNId!, command.Priority);
        if (moved.Succeeded && !Inbound.OpenSchedules(platform, trailer.NId).Any(s => s.LoadingStatus == LoadingStates.ToBeLoaded))
        {
            platform.Submit(Inbound.NewSchedule(platform, Inbound.Key(platform, "EMP-"), null, trailer, ScheduleTypes.Loading, "Empty", GateStates.Created, LoadingStates.ToBeLoaded));
        }

        return moved;
    }
}

/// <summary>Đổi điểm thả của yêu cầu đang mở; lúc Dropped xe tới đích mới.</summary>
[Command<YardOperationBlock>]
public sealed class ChangeMovementDestination : ICommand<YardResponse>
{
    [Mandatory]
    public string? MovementNId { get; init; }

    [Mandatory]
    public string? DestinationNId { get; init; }
}

internal sealed class ChangeMovementDestinationHandler : ICommandHandler<ChangeMovementDestination, YardResponse>
{
    public YardResponse Handle(ChangeMovementDestination command, IPlatform platform)
    {
        var movement = platform.Query<TrailerMovement>().SingleOrDefault(m => m.NId == command.MovementNId);
        if (movement is null || movement.Status == MovementStates.Dropped)
        {
            return YardResponse.Fail(NvErrors.InvalidMovement, $"Movement request {command.MovementNId} does not exist or is closed.");
        }

        if (!platform.ProjectionQuery<Equipment>().Any(e => e.NId == command.DestinationNId))
        {
            return YardResponse.Fail(NvErrors.UnknownLocation, $"Location {command.DestinationNId} does not exist.");
        }

        movement.ToNId = command.DestinationNId;
        platform.Submit(movement);
        return new YardResponse { NId = movement.NId, Status = movement.Status };
    }
}

/// <summary>
/// Nhận một skid ở dock. Kiểm theo thứ tự: độ dài nhãn; xe ở cửa dock; skid thuộc lịch đã tới của đúng xe; chưa nhận; logical dock của cửa
/// khớp mã dock trên nhãn; vị trí cất có thật; khác vị trí mặc định thì phải xác nhận. Tạo MTU; lịch Unloading; xe Tapped.
/// </summary>
[Command<YardOperationBlock>]
public sealed class ReceiveSkid : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? SkidBarcode { get; init; }

    [Mandatory]
    public string? StorageLocation { get; init; }

    public bool ConfirmNotDefault { get; init; }
}

internal sealed class ReceiveSkidHandler : ICommandHandler<ReceiveSkid, YardResponse>
{
    public YardResponse Handle(ReceiveSkid command, IPlatform platform)
    {
        if (!SkidLabel.TryParse(command.SkidBarcode, out var label))
        {
            return YardResponse.Fail(NvErrors.InvalidSkidLabel, $"Skid label must be exactly {SkidLabel.Length} characters; got {command.SkidBarcode?.Length ?? 0}.");
        }

        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (!Yard.IsDock(platform, trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.TrailerNotAtDock, $"Trailer {trailer.NId} is not at a dock door.");
        }

        if (trailer.Status is not (TrailerStates.Full or TrailerStates.Tapped))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status}.");
        }

        var schedules = Inbound.OpenSchedules(platform, trailer.NId).Where(s => s.GateStatus == GateStates.Arrived).ToList();
        var skid = Inbound.Match(platform, schedules.Select(s => s.NId), label);
        if (skid is null)
        {
            var elsewhere = platform.Query<InboundSkid>().Where(s => s.SupplierCode == label.Supplier && s.DockCode == label.Dock && s.PoNo == label.PoNo
                && s.PalletCode == label.Pallet && s.SkidNo == label.Skid).Select(s => s.ScheduleNId).FirstOrDefault();
            var other = elsewhere is null ? null : platform.Query<InboundSchedule>().Where(s => s.NId == elsewhere).Select(s => s.TrailerNId).SingleOrDefault();
            return other is null
                ? YardResponse.Fail(NvErrors.SkidNotDeclared, "The supplier has not declared this skid.")
                : YardResponse.Fail(NvErrors.SkidNotDeclared, $"This skid is on trailer {other}, not {trailer.NId}.");
        }

        if (skid.Verified)
        {
            return YardResponse.Fail(NvErrors.SkidAlreadyReceived, $"Skid {skid.SkidNo} of order {skid.PoNo} is already received.");
        }

        if (Inbound.Property(platform, trailer.LocationNId, "LogicalDock") != skid.DockCode)
        {
            return YardResponse.Fail(NvErrors.WrongLogicalDock, $"This skid has to be received at logical dock {skid.DockCode}.");
        }

        var location = Inbound.Location(platform, command.StorageLocation);
        if (location is null)
        {
            return YardResponse.Fail(NvErrors.UnknownLocation, $"Storage location {command.StorageLocation} does not exist.");
        }

        var preferred = Inbound.DefaultStore(platform, skid);
        if (preferred is not null && preferred != location && !command.ConfirmNotDefault)
        {
            return YardResponse.Fail(NvErrors.NotDefaultStorage, $"{location} is not the default location {preferred} of this part; confirm to store it there.");
        }

        if (Inbound.Receive(platform, skid, location) is { } failed)
        {
            return failed;
        }

        var schedule = schedules.Single(s => s.NId == skid.ScheduleNId);
        schedule.LoadingStatus = LoadingStates.Unloading;
        platform.Submit(schedule);
        Yard.SetStatus(platform, trailer, TrailerStates.Tapped);
        platform.Submit(trailer);
        return new YardResponse { NId = skid.MtuNId, Status = trailer.Status };
    }
}

/// <summary>Dỡ xong: lịch Unloaded, xe Empty. Còn skid chưa nhận thì phải xác nhận, và MOM gửi email danh sách skid đó.</summary>
[Command<YardOperationBlock>]
public sealed class CompleteUnloading : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    public bool ConfirmMissing { get; init; }
}

internal sealed class CompleteUnloadingHandler : ICommandHandler<CompleteUnloading, YardResponse>
{
    public YardResponse Handle(CompleteUnloading command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        var schedules = Inbound.OpenSchedules(platform, trailer.NId).Where(s => s.ScheduleType == ScheduleTypes.Unloading && s.GateStatus == GateStates.Arrived).ToList();
        if (trailer.Status is not (TrailerStates.Full or TrailerStates.Tapped) || schedules.Count == 0)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status} with no schedule to unload.");
        }

        var ids = schedules.Select(s => s.NId).ToList();
        var open = platform.Query<InboundSkid>().Where(s => ids.Contains(s.ScheduleNId!) && !s.Verified).ToList();
        if (open.Count > 0 && !command.ConfirmMissing)
        {
            return YardResponse.Fail(NvErrors.SkidsPending, $"{open.Count} skid(s) are not received; confirm to complete unloading.");
        }

        if (open.Count > 0)
        {
            var body = new StringBuilder("<UnreceivedSkids trailer=\"").Append(trailer.NId).Append("\">");
            foreach (var skid in open)
            {
                body.Append("<Skid po=\"").Append(skid.PoNo).Append("\" no=\"").Append(skid.SkidNo).Append("\"/>");
            }

            platform.SendMessageAsync(new OutboundMessage(ShipAdapters.Mail, "UnreceivedSkids", body.Append("</UnreceivedSkids>").ToString(), "application/xml"));
        }

        foreach (var schedule in schedules)
        {
            schedule.LoadingStatus = LoadingStates.Unloaded;
            platform.Submit(schedule);
        }

        Yard.SetStatus(platform, trailer, TrailerStates.Empty);
        platform.Submit(trailer);
        return YardResponse.Of(trailer);
    }
}

/// <summary>Đóng lịch của nhà cung cấp tại chỗ không có xe; lịch có xe đóng bằng dỡ xong để xe về rỗng.</summary>
[Command<YardOperationBlock>]
public sealed class CompleteLocalSchedule : ICommand<YardResponse>
{
    [Mandatory]
    public string? ScheduleNId { get; init; }
}

internal sealed class CompleteLocalScheduleHandler : ICommandHandler<CompleteLocalSchedule, YardResponse>
{
    public YardResponse Handle(CompleteLocalSchedule command, IPlatform platform)
    {
        var schedule = platform.Query<InboundSchedule>().SingleOrDefault(s => s.NId == command.ScheduleNId);
        if (schedule is null || GateStates.Closed.Contains(schedule.GateStatus))
        {
            return YardResponse.Fail(NvErrors.InvalidDelivery, $"Schedule {command.ScheduleNId} does not exist or is closed.");
        }

        if (platform.Query<InboundSkid>().Any(s => s.ScheduleNId == schedule.NId && !s.Verified))
        {
            return YardResponse.Fail(NvErrors.SkidsPending, $"Schedule {schedule.NId} has skids pending.");
        }

        schedule.LoadingStatus = LoadingStates.Unloaded;
        schedule.GateStatus = GateStates.Completed;
        platform.Submit(schedule);
        return new YardResponse { NId = schedule.NId, Status = schedule.GateStatus };
    }
}

/// <summary>
/// Chất hàng trả xong ở dock: phần trăm phải lớn hơn 0. Đóng lịch cũ, mở lịch chất hàng ForDeparture với placard trả hàng đúng route,
/// xe Full, và tạo yêu cầu kéo ra ô yard.
/// </summary>
[Command<YardOperationBlock>]
public sealed class ReportTrailerReloaded : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? ReturnRoute { get; init; }

    [Mandatory]
    public string? PlacardNId { get; init; }

    public decimal Percent { get; init; }

    [Mandatory]
    public string? DestinationNId { get; init; }

    public string? Priority { get; init; }
}

internal sealed class ReportTrailerReloadedHandler : ICommandHandler<ReportTrailerReloaded, YardResponse>
{
    public YardResponse Handle(ReportTrailerReloaded command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (command.Percent is <= 0 or > 100)
        {
            return YardResponse.Fail(NvErrors.InvalidReload, $"Percent reloaded must be above 0 and at most 100; got {command.Percent}.");
        }

        if (trailer.Status != TrailerStates.Empty || !Yard.IsDock(platform, trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status} at {trailer.LocationNId}; it must be empty at a dock door.");
        }

        Inbound.ReleasePlacard(platform, trailer);
        if (Inbound.Attach(platform, command.PlacardNId, PlacardTypes.ReturnComponent, command.ReturnRoute, trailer) is { } refused)
        {
            return refused;
        }

        foreach (var old in Inbound.OpenSchedules(platform, trailer.NId))
        {
            old.GateStatus = GateStates.CompletedReloaded;
            platform.Submit(old);
        }

        var schedule = Inbound.NewSchedule(platform, Inbound.Key(platform, "RLD-"), command.ReturnRoute, trailer, ScheduleTypes.Loading, TrailerContents.ComponentReturn, GateStates.ForDeparture, LoadingStates.Loaded);
        schedule.PlacardNId = command.PlacardNId;
        schedule.PercentReloaded = command.Percent;
        platform.Submit(schedule);

        trailer.ContentType = TrailerContents.ComponentReturn;
        trailer.Route = command.ReturnRoute;
        Yard.SetStatus(platform, trailer, TrailerStates.Full);
        var move = Yard.Move(platform, trailer, command.DestinationNId!, command.Priority);
        if (!move.Succeeded)
        {
            return move;
        }

        platform.Submit(trailer);
        return YardResponse.Of(trailer);
    }
}

/// <summary>In yard pass cho xe sắp rời (không gắn chuyến xuất hàng).</summary>
[Command<YardOperationBlock>]
public sealed class IssueTrailerYardPass : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }
}

internal sealed class IssueTrailerYardPassHandler : ICommandHandler<IssueTrailerYardPass, YardResponse>
{
    public YardResponse Handle(IssueTrailerYardPass command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (string.IsNullOrEmpty(trailer.LocationNId) || Yard.IsDock(platform, trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is not in a yard slot.");
        }

        trailer.YardPassNId = "YP-" + Counters.Base36(Counters.Take(platform, "YardPass") + 1).PadLeft(6, '0');
        platform.Submit(trailer);
        return new YardResponse { NId = trailer.YardPassNId, Status = trailer.Status };
    }
}

/// <summary>
/// Xe rời nhà máy. Xe của hãng vận chuyển chỉ cần xác nhận, không cần dỡ xong, đứng đâu cũng được, thành Deleted. Xe khác phải ở yard,
/// không còn yêu cầu kéo, có đúng yard pass; thành Free. Lịch Departed; xe hết vị trí, placard, yard pass.
/// </summary>
[Command<YardOperationBlock>]
public sealed class DepartTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    public string? YardPassNId { get; init; }
}

internal sealed class DepartTrailerHandler : ICommandHandler<DepartTrailer, YardResponse>
{
    public YardResponse Handle(DepartTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (trailer.Status is TrailerStates.Free or TrailerStates.Deleted)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status}; it is not in the plant.");
        }

        if (platform.Query<TrailerMovement>().Any(m => m.TrailerNId == trailer.NId && m.Status != MovementStates.Dropped))
        {
            return YardResponse.Fail(NvErrors.MovementOpen, $"Trailer {trailer.NId} has an open movement request.");
        }

        var carrierOwned = UnplannedTypes.CarrierOwned.Contains(trailer.ContentType);
        if (!carrierOwned)
        {
            if (Yard.IsDock(platform, trailer.LocationNId))
            {
                return YardResponse.Fail(NvErrors.TrailerAtDock, $"Trailer {trailer.NId} is still at {trailer.LocationNId}; move it out first.");
            }

            if (string.IsNullOrEmpty(trailer.YardPassNId) || trailer.YardPassNId != command.YardPassNId)
            {
                return YardResponse.Fail(NvErrors.NoYardPass, $"Yard pass {command.YardPassNId} is not the yard pass of trailer {trailer.NId}.");
            }
        }

        var now = platform.Time.GetUtcNow();
        foreach (var schedule in Inbound.OpenSchedules(platform, trailer.NId))
        {
            schedule.GateStatus = GateStates.Departed;
            schedule.DepartedOn = now;
            platform.Submit(schedule);
        }

        Inbound.ReleasePlacard(platform, trailer);
        trailer.LocationNId = null;
        trailer.YardPassNId = null;
        trailer.ContentType = null;
        trailer.Route = null;
        trailer.LastComment = null;
        Yard.SetStatus(platform, trailer, carrierOwned ? TrailerStates.Deleted : TrailerStates.Free);
        platform.Submit(trailer);
        return YardResponse.Of(trailer);
    }
}

/// <summary>Kiểm một nhãn khi quét ở màn chuyển skid: skid chưa nhận của xe nguồn thì ghi nhận được; không đổi dữ liệu.</summary>
[Command<YardOperationBlock>]
public sealed class CheckTransferSkid : ICommand<YardResponse>
{
    [Mandatory]
    public string? SourceBarcode { get; init; }

    [Mandatory]
    public string? SkidBarcode { get; init; }
}

internal sealed class CheckTransferSkidHandler : ICommandHandler<CheckTransferSkid, YardResponse>
{
    public YardResponse Handle(CheckTransferSkid command, IPlatform platform)
    {
        if (!SkidLabel.TryParse(command.SkidBarcode, out var label))
        {
            return YardResponse.Fail(NvErrors.InvalidSkidLabel, $"Skid label must be exactly {SkidLabel.Length} characters; got {command.SkidBarcode?.Length ?? 0}.");
        }

        var schedules = Inbound.OpenSchedules(platform, command.SourceBarcode!).Select(s => s.NId);
        var skid = Inbound.Match(platform, schedules, label);
        return skid is null || skid.Verified
            ? YardResponse.Fail(NvErrors.SkidNotOnSource, $"No unreceived skid on trailer {command.SourceBarcode} matches this label.")
            : new YardResponse { NId = skid.NId };
    }
}

/// <summary>
/// Chuyển skid chưa nhận giữa hai xe đang ở dock. Skid sang lịch của xe đích theo route gốc, nhãn không đổi, ghi lịch sử. Xe nguồn hết
/// skid thì rỗng, có lịch chất hàng mới. Xe đích rỗng thành Full với placard mới (Tapped nếu còn skid đã nhận).
/// </summary>
[Command<YardOperationBlock>]
public sealed class TransferSkids : ICommand<YardResponse>
{
    [Mandatory]
    public string? SourceBarcode { get; init; }

    [Mandatory]
    public string? DestinationBarcode { get; init; }

    [Mandatory]
    public string? ReasonNId { get; init; }

    [Mandatory]
    public string? Comments { get; init; }

    public IReadOnlyList<string>? SkidBarcodes { get; init; }
}

internal sealed class TransferSkidsHandler : ICommandHandler<TransferSkids, YardResponse>
{
    public YardResponse Handle(TransferSkids command, IPlatform platform)
    {
        if (command.SourceBarcode == command.DestinationBarcode)
        {
            return YardResponse.Fail(NvErrors.InvalidTransfer, "Source and destination must be two different trailers.");
        }

        if (!platform.Query<TransferReason>().Any(r => r.NId == command.ReasonNId))
        {
            return YardResponse.Fail(NvErrors.InvalidTransfer, $"Transfer reason {command.ReasonNId} does not exist.");
        }

        if (command.SkidBarcodes is null || command.SkidBarcodes.Count == 0)
        {
            return YardResponse.Fail(NvErrors.InvalidTransfer, "Scan at least one skid.");
        }

        if (Yard.Find(platform, command.SourceBarcode!, out var source) is { } noSource)
        {
            return noSource;
        }

        if (Yard.Find(platform, command.DestinationBarcode!, out var destination) is { } noDestination)
        {
            return noDestination;
        }

        var sourceSchedules = Inbound.OpenSchedules(platform, source.NId).Where(s => s.GateStatus == GateStates.Arrived).ToList();
        if (!Yard.IsDock(platform, source.LocationNId) || source.Status is not (TrailerStates.Full or TrailerStates.Tapped)
            || source.ContentType != TrailerContents.Component || sourceSchedules.Count == 0)
        {
            return YardResponse.Fail(NvErrors.InvalidTransfer, $"Source trailer {source.NId} must be a full or tapped component trailer at a dock door with an arrived schedule.");
        }

        var destinationSchedules = Inbound.OpenSchedules(platform, destination.NId);
        if (!Yard.IsDock(platform, destination.LocationNId) || destination.Status is not (TrailerStates.Full or TrailerStates.Tapped or TrailerStates.Empty)
            || !(string.IsNullOrEmpty(destination.ContentType) || destination.ContentType == TrailerContents.Component) || destinationSchedules.Count == 0)
        {
            return YardResponse.Fail(NvErrors.InvalidTransfer, $"Destination trailer {destination.NId} must be a component or empty trailer at a dock door with an open schedule.");
        }

        var moving = new List<InboundSkid>();
        foreach (var barcode in command.SkidBarcodes)
        {
            if (!SkidLabel.TryParse(barcode, out var label))
            {
                return YardResponse.Fail(NvErrors.InvalidSkidLabel, $"Skid label must be exactly {SkidLabel.Length} characters; got {barcode.Length}.");
            }

            var skid = Inbound.Match(platform, sourceSchedules.Select(s => s.NId), label);
            if (skid is null || skid.Verified)
            {
                return YardResponse.Fail(NvErrors.SkidNotOnSource, $"No unreceived skid on trailer {source.NId} matches label {barcode}.");
            }

            if (moving.All(m => m.NId != skid.NId))
            {
                moving.Add(skid);
            }
        }

        var destinationWasEmpty = destination.Status == TrailerStates.Empty;
        if (destinationWasEmpty)
        {
            foreach (var waiting in destinationSchedules.Where(s => s.LoadingStatus == LoadingStates.ToBeLoaded))
            {
                waiting.GateStatus = GateStates.CompletedReloaded;
                platform.Submit(waiting);
            }

            destinationSchedules = [.. destinationSchedules.Where(s => s.LoadingStatus != LoadingStates.ToBeLoaded)];
            Inbound.ReleasePlacard(platform, destination);
            destination.PlacardNId = Inbound.NewPlacard(platform, PlacardTypes.Component, moving[0].Route, destination.NId);
        }

        var created = new List<InboundSchedule>();
        var now = platform.Time.GetUtcNow();
        foreach (var skid in moving)
        {
            var from = sourceSchedules.Single(s => s.NId == skid.ScheduleNId);
            var target = destinationSchedules.Concat(created).FirstOrDefault(s => s.Route == from.Route && s.GateStatus == GateStates.Arrived);
            if (target is null)
            {
                target = Inbound.NewSchedule(platform, Inbound.Key(platform, "TRF-"), from.Route, destination, ScheduleTypes.Unloading, TrailerContents.Component, GateStates.Arrived, LoadingStates.ToBeUnloaded);
                target.PlacardNId = destination.PlacardNId;
                target.ArrivedOn = now;
                target.WithTransferredSkids = true;
                platform.Submit(target);
                created.Add(target);
            }

            if (!platform.Query<SkidTransload>().Any(t => t.SkidNId == skid.NId))
            {
                Record(platform, skid, "Arrival", null, source.NId, null, null);
            }

            Record(platform, skid, "Transload", source.NId, destination.NId, command.ReasonNId, from.Route);
            skid.ScheduleNId = target.NId;
            platform.Submit(skid);
        }

        return Finish(platform, command, source, destination, sourceSchedules, destinationWasEmpty, created);
    }

    private static YardResponse Finish(IPlatform platform, TransferSkids command, Trailer source, Trailer destination, List<InboundSchedule> sourceSchedules, bool destinationWasEmpty, List<InboundSchedule> created)
    {
        var ids = sourceSchedules.Select(s => s.NId).ToList();
        var left = platform.Query<InboundSkid>().Where(s => ids.Contains(s.ScheduleNId!)).ToList();
        foreach (var schedule in sourceSchedules.Where(s => !left.Any(k => k.ScheduleNId == s.NId && !k.Verified)))
        {
            schedule.GateStatus = GateStates.CompletedReloaded;
            schedule.LoadingStatus = LoadingStates.Unloaded;
            platform.Submit(schedule);
        }

        if (left.All(s => s.Verified))
        {
            source.ContentType = null;
            Yard.SetStatus(platform, source, TrailerStates.Empty);
            platform.Submit(Inbound.NewSchedule(platform, Inbound.Key(platform, "EMP-"), null, source, ScheduleTypes.Loading, "Empty", GateStates.Created, LoadingStates.ToBeLoaded));
            source.Route = null;
        }
        else
        {
            Inbound.RefreshRoutes(platform, source);
        }

        Inbound.Comment(source, $"{command.Comments}.[Skids moved to trailer {destination.NId}]");
        platform.Submit(source);

        if (destinationWasEmpty)
        {
            var kept = platform.Query<InboundSkid>().Any(s => s.Verified && Inbound.OpenSchedules(platform, destination.NId).Select(o => o.NId).Contains(s.ScheduleNId!));
            destination.ContentType = TrailerContents.Component;
            Yard.SetStatus(platform, destination, kept ? TrailerStates.Tapped : TrailerStates.Full);
        }

        Inbound.RefreshRoutes(platform, destination, created);
        Inbound.Comment(destination, $"{command.Comments}.[Skids moved from trailer {source.NId}]");
        platform.Submit(destination);
        return YardResponse.Of(destination);
    }

    private static void Record(IPlatform platform, InboundSkid skid, string kind, string? from, string? to, string? reason, string? route)
    {
        var entry = platform.Create<SkidTransload>();
        entry.NId = entry.Id.ToString("N");
        entry.SkidNId = skid.NId;
        entry.Kind = kind;
        entry.FromTrailerNId = from;
        entry.ToTrailerNId = to;
        entry.ReasonNId = reason;
        entry.OldRoute = route;
        platform.Submit(entry);
    }
}

public sealed class NoYardParameters;

public sealed class TrailerParameters
{
    public string? Barcode { get; init; }
}

public sealed class UnplannedParameters
{
    public string? TrailerType { get; init; }
}

/// <summary>Lưới đăng ký ở cổng: lịch linh kiện chưa tới, có ít nhất một dòng của nhà cung cấp thường.</summary>
[ReadingFunction(YardApp.Name)]
public sealed class ArrivalSchedules : IReadingFunction<NoYardParameters, InboundSchedule>
{
    public IQueryable<InboundSchedule> Read(NoYardParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var local = platform.ProjectionQuery<LocalSupplier>().Select(s => s.NId);
        var regular = platform.ProjectionQuery<InboundSkid>().Where(s => !local.Contains(s.SupplierCode!)).Select(s => s.ScheduleNId);
        return platform.ProjectionQuery<InboundSchedule>().Where(s => (s.GateStatus == GateStates.Planned || s.GateStatus == GateStates.InTransit) && regular.Contains(s.NId));
    }
}

/// <summary>Skid đang thuộc các lịch mở của một xe, gồm cả skid đã nhận.</summary>
[ReadingFunction(YardApp.Name)]
public sealed class TrailerSkids : IReadingFunction<TrailerParameters, InboundSkid>
{
    public IQueryable<InboundSkid> Read(TrailerParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var schedules = platform.ProjectionQuery<InboundSchedule>()
            .Where(s => s.TrailerNId == parameters.Barcode && !GateStates.Closed.Contains(s.GateStatus!)).Select(s => s.NId);
        return platform.ProjectionQuery<InboundSkid>().Where(s => schedules.Contains(s.ScheduleNId!));
    }
}

/// <summary>Ô dock, yard còn trống cho một loại xe không lịch: không có xe, không là đích của yêu cầu đang mở, đúng nhóm.</summary>
[ReadingFunction(YardApp.Name)]
public sealed class UnplannedLocations : IReadingFunction<UnplannedParameters, Equipment>
{
    public IQueryable<Equipment> Read(UnplannedParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var group = parameters.TrailerType switch
        {
            UnplannedTypes.DirectMaterial => "DirectArea",
            UnplannedTypes.AdHocComponents => "AdHocArea",
            UnplannedTypes.CustomerEmpty => "EmptyArea",
            _ => null,
        };
        var properties = platform.ProjectionQuery<EquipmentProperty>();
        var locations = group is null
            ? properties.Where(p => p.Name == "LocationType" && !properties.Any(g => g.EquipmentNId == p.EquipmentNId && g.Name == "ArrivalGroup" && g.Value == "EmptyArea")).Select(p => p.EquipmentNId)
            : properties.Where(p => p.Name == "ArrivalGroup" && p.Value == group).Select(p => p.EquipmentNId);
        var busy = platform.ProjectionQuery<Trailer>().Select(t => t.LocationNId);
        var targets = platform.ProjectionQuery<TrailerMovement>().Where(m => m.Status != MovementStates.Dropped).Select(m => m.ToNId);
        return platform.ProjectionQuery<Equipment>().Where(e => locations.Contains(e.NId) && !busy.Contains(e.NId) && !targets.Contains(e.NId));
    }
}

/// <summary>Yêu cầu kéo đang mở mà người gọi được thấy: đích hoặc nguồn thuộc khu được gán; chưa gán khu thì thấy tất cả.</summary>
[ReadingFunction(YardApp.Name)]
public sealed class MyTrailerMovements : IReadingFunction<NoYardParameters, TrailerMovement>
{
    public IQueryable<TrailerMovement> Read(NoYardParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var open = platform.ProjectionQuery<TrailerMovement>().Where(m => m.Status != MovementStates.Dropped);
        var user = platform.Caller.User;
        var zones = platform.ProjectionQuery<ShuntZone>().Where(z => z.UserNId == user).Select(z => z.AreaNId);
        if (!zones.Any())
        {
            return open;
        }

        var inZone = platform.ProjectionQuery<Equipment>().Where(e => zones.Contains(e.ParentNId)).Select(e => e.NId);
        return open.Where(m => inZone.Contains(m.ToNId) || inZone.Contains(m.FromNId));
    }
}

/// <summary>Khai lý do chuyển skid (master data).</summary>
[Command<YardOperationBlock>]
public sealed class SetTransferReason : ICommand<YardResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class SetTransferReasonHandler : ICommandHandler<SetTransferReason, YardResponse>
{
    public YardResponse Handle(SetTransferReason command, IPlatform platform)
    {
        if (!platform.Query<TransferReason>().Any(r => r.NId == command.NId))
        {
            var reason = platform.Create<TransferReason>();
            reason.NId = command.NId!;
            platform.Submit(reason);
        }

        return new YardResponse { NId = command.NId };
    }
}

/// <summary>Khai nhà cung cấp tại chỗ; có material thì thêm material đó vào danh sách tự nhận.</summary>
[Command<YardOperationBlock>]
public sealed class SetLocalSupplier : ICommand<YardResponse>
{
    [Mandatory]
    public string? SupplierCode { get; init; }

    public string? MaterialNId { get; init; }
}

internal sealed class SetLocalSupplierHandler : ICommandHandler<SetLocalSupplier, YardResponse>
{
    public YardResponse Handle(SetLocalSupplier command, IPlatform platform)
    {
        if (!platform.Query<LocalSupplier>().Any(s => s.NId == command.SupplierCode))
        {
            var supplier = platform.Create<LocalSupplier>();
            supplier.NId = command.SupplierCode!;
            platform.Submit(supplier);
        }

        var nid = command.SupplierCode + "|" + command.MaterialNId;
        if (!string.IsNullOrEmpty(command.MaterialNId) && !platform.Query<LocalSupplierMaterial>().Any(m => m.NId == nid))
        {
            var material = platform.Create<LocalSupplierMaterial>();
            material.NId = nid;
            material.SupplierCode = command.SupplierCode;
            material.MaterialNId = command.MaterialNId;
            platform.Submit(material);
        }

        return new YardResponse { NId = command.SupplierCode };
    }
}

/// <summary>Gán một khu bãi cho người kéo xe.</summary>
[Command<YardOperationBlock>]
public sealed class AssignShuntZone : ICommand<YardResponse>
{
    [Mandatory]
    public string? UserNId { get; init; }

    [Mandatory]
    public string? AreaNId { get; init; }
}

internal sealed class AssignShuntZoneHandler : ICommandHandler<AssignShuntZone, YardResponse>
{
    public YardResponse Handle(AssignShuntZone command, IPlatform platform)
    {
        var nid = command.UserNId + "|" + command.AreaNId;
        if (!platform.Query<ShuntZone>().Any(z => z.NId == nid))
        {
            var zone = platform.Create<ShuntZone>();
            zone.NId = nid;
            zone.UserNId = command.UserNId;
            zone.AreaNId = command.AreaNId;
            platform.Submit(zone);
        }

        return new YardResponse { NId = nid };
    }
}
