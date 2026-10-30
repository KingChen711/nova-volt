using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Xuất hàng, nửa sau: ngoại lệ khi chất xe (họ B), chất xe (họ B chờ CustomerShipSys xác nhận qua hai tin đồng bộ), vận đơn, xe của
// khách vào bãi, vào dock, ra bãi, yard pass, xe rời (đơn Shipped, pallet về Empty, hàng Shipped) và ASN cho họ A.

/// <summary>Nhóm ngoại lệ, theo thứ tự màn hỏi khi bấm chất xe.</summary>
public static class ExceptionGroups
{
    /// <summary>Mức kiện: giao bổ sung sau một chuyến cùng nơi nhận, route, giờ giao đã đi.</summary>
    public const string SkidLevel = "SkidLevel";

    /// <summary>Đóng kiện: một đơn của khách tại một dock staged ít hơn kế hoạch.</summary>
    public const string SkidBuild = "SkidBuild";

    /// <summary>Cả chuyến: một đơn của chuyến không có hàng staged; hoặc mã tự khai.</summary>
    public const string Trailer = "Trailer";

    public static readonly IReadOnlyList<string> Order = [SkidLevel, SkidBuild, Trailer];
}

/// <summary>Danh mục mã ngoại lệ (master data). Mã tự khai không hiện cho người dùng chọn.</summary>
[Entity<ShipOperationBlock>]
public sealed class LoadingExceptionCode : Entity
{
    [Mandatory]
    public string? Group { get; set; }

    public string? Description { get; set; }

    /// <summary>MOM tự khai mã này, không hỏi; có nó thì route gửi CustomerShipSys mang tiền tố <c>EX-</c>.</summary>
    public bool Automatic { get; set; }
}

/// <summary>Ngoại lệ đã khai cho một đơn của chuyến. NId = <c>&lt;đơn&gt;|&lt;mã&gt;</c>: một đơn không khai trùng mã.</summary>
[Entity<ShipOperationBlock>]
public sealed class LoadingExceptionEntry : Entity
{
    [Mandatory]
    public string? OrderNId { get; set; }

    [Mandatory]
    public string? DeliveryNId { get; set; }

    [Mandatory]
    public string? CodeNId { get; set; }

    public string? Group { get; set; }

    public string? Dock { get; set; }

    [TextLength(-1)]
    public string? Comment { get; set; }
}

/// <summary>ASN đã gửi cho khách họ A khi xe rời, và phản hồi của khách. NId = số vận đơn.</summary>
[Entity<ShipOperationBlock>]
public sealed class AsnDocument : Entity
{
    public string? DeliveryNId { get; set; }

    public string? ShipTo { get; set; }

    public DateTimeOffset? SentOn { get; set; }

    public string? ResponseStatus { get; set; }

    [TextLength(-1)]
    public string? ResponseMessage { get; set; }
}

/// <summary>Xe của chuyến giao đã rời nhà máy. Phát sau commit.</summary>
[Event<ShipOperationBlock>]
[EventVersion(1)]
public sealed record DeliveryDeparted(string DeliveryNId) : IFoundationEvent;

public sealed class LoadingCheckResponse : CommandResponse
{
    /// <summary>Không thiếu ngoại lệ và xe đã ở cửa dock: mở được trang chất xe.</summary>
    public bool Ready { get; set; }

    /// <summary>Nhóm ngoại lệ đầu tiên còn thiếu; rỗng khi không thiếu.</summary>
    public string? MissingGroup { get; set; }

    public string? OrderNId { get; set; }

    public string? CustomerOrderNo { get; set; }

    public string? Dock { get; set; }

    /// <summary>Mã người dùng được chọn: chỉ mã của nhóm còn thiếu.</summary>
    public List<string> Codes { get; set; } = [];

    /// <summary>Mã MOM vừa tự khai.</summary>
    public List<string> Declared { get; set; } = [];

    internal static LoadingCheckResponse Fail(int code, string message)
    {
        var response = new LoadingCheckResponse();
        response.SetError(code, message);
        return response;
    }
}

internal static class LoadingRules
{
    public sealed record Missing(string Group, SalesOrder Order, string? CustomerOrderNo, string? Dock);

    /// <summary>
    /// Ngoại lệ còn thiếu của chuyến họ B, theo thứ tự SkidLevel, SkidBuild, Trailer; trước đó tự khai mã tự động. Chuyến họ A không
    /// có ngoại lệ.
    /// </summary>
    public static Missing? FirstMissing(IPlatform platform, Delivery delivery, List<string> declared)
    {
        var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
        var orders = Shipments.Orders(platform, group.NId).Where(o => o.Family == ShipFamilies.B).ToList();
        if (orders.Count == 0)
        {
            return null;
        }

        var lines = Shipments.Lines(platform, group.NId);
        var existing = platform.Query<LoadingExceptionEntry>().Where(e => e.DeliveryNId == delivery.NId).ToList();
        var codes = platform.Query<LoadingExceptionCode>().ToList();

        // Mã tự khai: đơn khách đã giao một phần trên route này (dòng đã Shipped cùng mã đơn khách ở nhóm khác) mà chuyến này
        // vẫn có hàng staged của đơn đó.
        foreach (var auto in codes.Where(c => c.Automatic))
        {
            foreach (var order in orders.Where(o => existing.All(e => e.OrderNId != o.NId || e.CodeNId != auto.NId)))
            {
                var staged = lines.Where(l => l.OrderNId == order.NId && l.StagedQuantity > 0).ToList();
                var customerOrders = staged.Select(l => l.CustomerOrderNo).ToList();
                var shippedBefore = platform.Query<SalesOrderLine>().Where(l => customerOrders.Contains(l.CustomerOrderNo) && l.ShippedQuantity > 0)
                    .Select(l => l.OrderNId).ToList();
                if (shippedBefore.Count == 0 || !platform.Query<SalesOrder>().Any(o => shippedBefore.Contains(o.NId) && o.Route == order.Route && o.GroupNId != group.NId))
                {
                    continue;
                }

                var declaredAuto = Declare(platform, delivery, order, auto, staged[0].Dock, "Declared by MOM: partial shipment of the customer order.");
                existing.Add(declaredAuto);
                declared.Add(auto.NId);
            }
        }

        var anyStaged = lines.Any(l => l.StagedQuantity > 0);
        var shippedTwin = platform.Query<PickListGroup>().Any(g => g.NId != group.NId && g.ShipTo == group.ShipTo && g.Route == group.Route
            && g.DeliverOn == group.DeliverOn && g.Status == OrderStates.Shipped);
        if (anyStaged && shippedTwin && !existing.Any(e => e.Group == ExceptionGroups.SkidLevel))
        {
            return new Missing(ExceptionGroups.SkidLevel, orders[0], null, null);
        }

        foreach (var line in lines.Where(l => l.StagedQuantity > 0 && l.StagedQuantity < l.Quantity))
        {
            var order = orders.SingleOrDefault(o => o.NId == line.OrderNId);
            if (order is not null && !existing.Any(e => e.OrderNId == order.NId && e.Group == ExceptionGroups.SkidBuild && e.Dock == line.Dock))
            {
                return new Missing(ExceptionGroups.SkidBuild, order, line.CustomerOrderNo, line.Dock);
            }
        }

        foreach (var order in orders.Where(o => lines.Where(l => l.OrderNId == o.NId).Sum(l => l.StagedQuantity) == 0))
        {
            if (!existing.Any(e => e.OrderNId == order.NId && e.Group == ExceptionGroups.Trailer))
            {
                var line = lines.First(l => l.OrderNId == order.NId);
                return new Missing(ExceptionGroups.Trailer, order, line.CustomerOrderNo, line.Dock);
            }
        }

        return null;
    }

    public static LoadingExceptionEntry Declare(IPlatform platform, Delivery delivery, SalesOrder order, LoadingExceptionCode code, string? dock, string? comment)
    {
        var item = platform.Create<LoadingExceptionEntry>();
        item.NId = order.NId + "|" + code.NId;
        item.OrderNId = order.NId;
        item.DeliveryNId = delivery.NId;
        item.CodeNId = code.NId;
        item.Group = code.Group;
        item.Dock = dock;
        item.Comment = comment;
        platform.Submit(item);
        return item;
    }

    /// <summary>Xe của chuyến đang đứng ở một cửa dock.</summary>
    public static Trailer? TrailerAtDock(IPlatform platform, Delivery delivery)
    {
        var trailer = delivery.TrailerNId is null ? null : platform.Query<Trailer>().SingleOrDefault(t => t.NId == delivery.TrailerNId);
        return trailer is not null && Yard.IsDock(platform, trailer.LocationNId) ? trailer : null;
    }
}

/// <summary>
/// Nút chất xe ở màn staging. Họ B: tự khai mã tự động, rồi báo nhóm ngoại lệ đầu tiên còn thiếu kèm các mã được chọn (màn hiện hộp
/// khai ngoại lệ); khai xong thì gọi lại. Không thiếu ngoại lệ thì kiểm xe đã ở cửa dock. Họ A đi thẳng tới bước kiểm xe.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class CheckTruckLoading : ICommand<LoadingCheckResponse>
{
    [Mandatory]
    public string? DeliveryNId { get; init; }
}

internal sealed class CheckTruckLoadingHandler : ICommandHandler<CheckTruckLoading, LoadingCheckResponse>
{
    public LoadingCheckResponse Handle(CheckTruckLoading command, IPlatform platform)
    {
        var delivery = platform.Query<Delivery>().SingleOrDefault(d => d.NId == command.DeliveryNId);
        if (delivery is null)
        {
            return LoadingCheckResponse.Fail(NvErrors.UnknownShipment, $"Delivery {command.DeliveryNId} does not exist.");
        }

        var declared = new List<string>();
        if (LoadingRules.FirstMissing(platform, delivery, declared) is { } missing)
        {
            return new LoadingCheckResponse
            {
                MissingGroup = missing.Group,
                OrderNId = missing.Order.NId,
                CustomerOrderNo = missing.CustomerOrderNo ?? missing.Order.CustomerReference,
                Dock = missing.Dock,
                Declared = declared,
                Codes = [.. platform.Query<LoadingExceptionCode>().Where(c => c.Group == missing.Group && !c.Automatic).OrderBy(c => c.NId).Select(c => c.NId)],
            };
        }

        if (LoadingRules.TrailerAtDock(platform, delivery) is null)
        {
            var failed = LoadingCheckResponse.Fail(NvErrors.NoTrailerAtDock, $"Delivery {delivery.NId} has no trailer at a dock.");
            failed.Declared = declared;
            return failed;
        }

        if (Shipments.FamilyOf(platform, delivery.GroupNId!) == ShipFamilies.B)
        {
            // Mở trang chất xe khi chuyến mới staged một phần: gửi lại email danh sách pallet.
            var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
            if (group.Status != OrderStates.Staged)
            {
                Staging.MailStagedPallets(platform, group, delivery);
            }
        }

        return new LoadingCheckResponse { Ready = true, Declared = declared };
    }
}

/// <summary>Khai một ngoại lệ cho một đơn của chuyến (hộp khai ngoại lệ, trang Exception). Mã phải có trong danh mục, không tự khai.</summary>
[Command<ShipOperationBlock>]
public sealed class AddLoadingExceptionEntry : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? DeliveryNId { get; init; }

    [Mandatory]
    public string? OrderNId { get; init; }

    [Mandatory]
    public string? Code { get; init; }

    public string? Dock { get; init; }

    public string? Comment { get; init; }
}

internal sealed class AddLoadingExceptionEntryHandler : ICommandHandler<AddLoadingExceptionEntry, ShipmentResponse>
{
    public ShipmentResponse Handle(AddLoadingExceptionEntry command, IPlatform platform)
    {
        if (Shipments.Delivery(platform, command.DeliveryNId!, out var delivery) is { } missing)
        {
            return missing;
        }

        var order = platform.Query<SalesOrder>().SingleOrDefault(o => o.NId == command.OrderNId && o.GroupNId == delivery.GroupNId);
        if (order is null)
        {
            return ShipmentResponse.Fail(NvErrors.UnknownShipment, $"Order {command.OrderNId} is not in delivery {delivery.NId}.");
        }

        var code = platform.Query<LoadingExceptionCode>().SingleOrDefault(c => c.NId == command.Code && !c.Automatic);
        if (code is null)
        {
            return ShipmentResponse.Fail(NvErrors.UnknownExceptionCode, $"Exception code {command.Code} is not in the catalog.");
        }

        if (platform.Query<LoadingExceptionEntry>().Any(e => e.OrderNId == order.NId && e.CodeNId == code.NId))
        {
            return ShipmentResponse.Fail(NvErrors.DuplicateException, $"Order {order.NId} already has exception {code.NId}.");
        }

        var item = LoadingRules.Declare(platform, delivery, order, code, command.Dock, command.Comment);
        return new ShipmentResponse { NId = item.NId, Status = item.Group, DeliveryNId = delivery.NId };
    }
}

/// <summary>Xóa một ngoại lệ đã khai (trang Exception), trước khi chất xe.</summary>
[Command<ShipOperationBlock>]
public sealed class DeleteLoadingExceptionEntry : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? ExceptionNId { get; init; }
}

internal sealed class DeleteLoadingExceptionEntryHandler : ICommandHandler<DeleteLoadingExceptionEntry, ShipmentResponse>
{
    public ShipmentResponse Handle(DeleteLoadingExceptionEntry command, IPlatform platform)
    {
        var item = platform.Query<LoadingExceptionEntry>().SingleOrDefault(e => e.NId == command.ExceptionNId);
        if (item is null)
        {
            return ShipmentResponse.Fail(NvErrors.UnknownExceptionCode, $"Exception {command.ExceptionNId} does not exist.");
        }

        if (platform.Query<Delivery>().Any(d => d.NId == item.DeliveryNId && d.Status != DeliveryStates.New && d.Status != DeliveryStates.Arrived))
        {
            return ShipmentResponse.Fail(NvErrors.InvalidShipmentState, $"Delivery {item.DeliveryNId} is loaded; its exceptions are kept.");
        }

        platform.Delete(item);
        return new ShipmentResponse { NId = command.ExceptionNId };
    }
}

/// <summary>Mã ngoại lệ trong danh mục (master data).</summary>
[Command<ShipOperationBlock>]
public sealed class SetLoadingExceptionCode : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? Code { get; init; }

    [Mandatory]
    public string? Group { get; init; }

    public string? Description { get; init; }

    public bool Automatic { get; init; }
}

internal sealed class SetLoadingExceptionCodeHandler : ICommandHandler<SetLoadingExceptionCode, ShipmentResponse>
{
    public ShipmentResponse Handle(SetLoadingExceptionCode command, IPlatform platform)
    {
        if (!ExceptionGroups.Order.Contains(command.Group!))
        {
            return ShipmentResponse.Fail(NvErrors.UnknownExceptionCode, $"Exception group {command.Group} is not one of {string.Join(", ", ExceptionGroups.Order)}.");
        }

        var code = platform.Query<LoadingExceptionCode>().SingleOrDefault(c => c.NId == command.Code) ?? platform.Create<LoadingExceptionCode>();
        code.NId = command.Code!;
        code.Group = command.Group;
        code.Description = command.Description;
        code.Automatic = command.Automatic;
        platform.Submit(code);
        return new ShipmentResponse { NId = code.NId, Status = code.Group };
    }
}

/// <summary>
/// Chất các skid vừa quét lên xe (Skid Load → Confirm). Skid phải Staged, thuộc chuyến; xe của chuyến ở cửa dock; họ B không còn thiếu
/// ngoại lệ. Họ B: gửi đồng bộ tin SkidBuild rồi ShipLoad tới CustomerShipSys (kèm ngoại lệ), phải nhận số xác nhận và số chuyến;
/// không nhận được thì lệnh lỗi và không ghi gì (pallet giữ Staged). Rồi skid và pallet Loaded, đơn Loading; hết skid staged thì
/// chuyến Loaded, xe CustomerLoaded, in vận đơn.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class ConfirmTruckLoad : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? DeliveryNId { get; init; }

    [Mandatory]
    public string? SealNo { get; init; }

    /// <summary>Mã nhãn skid vừa quét; nhãn họ B mặt A hay B đều được.</summary>
    [Mandatory]
    public List<string>? Skids { get; init; }
}

internal sealed class ConfirmTruckLoadHandler : ICommandHandler<ConfirmTruckLoad, ShipmentResponse>
{
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    public ShipmentResponse Handle(ConfirmTruckLoad command, IPlatform platform)
    {
        if (Shipments.Delivery(platform, command.DeliveryNId!, out var delivery) is { } missing)
        {
            return missing;
        }

        if (LoadingRules.TrailerAtDock(platform, delivery) is not { } trailer)
        {
            return ShipmentResponse.Fail(NvErrors.NoTrailerAtDock, $"Delivery {delivery.NId} has no trailer at a dock.");
        }

        var skids = new List<Skid>();
        foreach (var label in command.Skids!.Select(s => s.Trim()).Distinct(StringComparer.Ordinal))
        {
            var key = ShipBarcodes.ParseSkid(label) is not null ? delivery.NId + "|" + label[..ShipBarcodes.SkidKeyLength] : label;
            var skid = platform.Query<Skid>().SingleOrDefault(s => s.NId == key && s.DeliveryNId == delivery.NId);
            if (skid is null || skid.Status != SkidStates.Staged)
            {
                return ShipmentResponse.Fail(NvErrors.SkidNotStaged, $"{label} is not a staged skid of delivery {delivery.NId}.");
            }

            skids.Add(skid);
        }

        if (skids.Count == 0)
        {
            return ShipmentResponse.Fail(NvErrors.SkidNotStaged, "Scan at least one skid.");
        }

        var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
        if (Shipments.FamilyOf(platform, group.NId) == ShipFamilies.B)
        {
            if (LoadingRules.FirstMissing(platform, delivery, []) is { } open)
            {
                return ShipmentResponse.Fail(NvErrors.ExceptionRequired, $"Order {open.Order.NId} needs a {open.Group} exception before loading.");
            }

            if (Confirm(platform, delivery, group, trailer, command.SealNo!, skids) is { } failed)
            {
                return failed;
            }
        }

        foreach (var skid in skids)
        {
            var loaded = platform.CallCommand<SetMtuaStatus, ProductResponse>(new SetMtuaStatus { NId = skid.PalletNId, Status = PalletStates.Loaded });
            if (!loaded.Succeeded)
            {
                return ShipmentResponse.From(loaded);
            }

            skid.Status = SkidStates.Loaded;
            platform.Submit(skid);
            var picked = platform.Query<PickedPallet>().Single(p => p.NId == group.NId + "|" + skid.PalletNId);
            picked.Status = PalletStates.Loaded;
            platform.Submit(picked);
            var line = platform.Query<SalesOrderLine>().Single(l => l.NId == skid.OrderLineNId);
            line.LoadedQuantity += picked.Quantity;
            platform.Submit(line);
        }

        delivery.SealNo = command.SealNo;
        group.Status = OrderStates.Loading;
        platform.Submit(group);
        Shipments.SetOrders(platform, group.NId, OrderStates.Loading);
        if (!platform.Query<Skid>().Any(s => s.DeliveryNId == delivery.NId && s.Status == SkidStates.Staged))
        {
            delivery.Status = DeliveryStates.Loaded;
            delivery.BillOfLadingNo = Shipments.Counter(platform, "BL", "BillOfLading", 8);
            Yard.SetStatus(platform, trailer, TrailerStates.CustomerLoaded);
            platform.Submit(trailer);
            BillOfLading(platform, delivery, group, trailer);
        }

        platform.Submit(delivery);
        var (todo, completed) = Shipments.Progress(platform, delivery);
        return new ShipmentResponse
        {
            NId = delivery.NId,
            Status = delivery.Status,
            GroupNId = group.NId,
            DeliveryNId = delivery.NId,
            Todo = todo,
            Completed = completed,
        };
    }

    /// <summary>Hai tin đồng bộ tới CustomerShipSys. Trả lỗi khi một tin không được trả lời hoặc thiếu số xác nhận, số chuyến.</summary>
    private static ShipmentResponse? Confirm(IPlatform platform, Delivery delivery, PickListGroup group, Trailer trailer, string seal, List<Skid> skids)
    {
        var exceptions = platform.Query<LoadingExceptionEntry>().Where(e => e.DeliveryNId == delivery.NId).ToList();
        var lines = Shipments.Lines(platform, group.NId).ToDictionary(l => l.NId);
        var skidBuild = new JsonObject
        {
            ["delivery"] = delivery.NId,
            ["route"] = group.Route,
            ["orders"] = new JsonArray([.. skids.GroupBy(s => lines[s.OrderLineNId!].CustomerOrderNo + "|" + lines[s.OrderLineNId!].Dock).Select(g =>
            {
                var line = lines[g.First().OrderLineNId!];
                return (JsonNode)new JsonObject
                {
                    ["customerOrder"] = line.CustomerOrderNo,
                    ["dock"] = line.Dock,
                    ["supplier"] = line.SupplierCode,
                    ["skids"] = new JsonArray([.. g.Select(s => (JsonNode)new JsonObject { ["skid"] = s.SkidNo, ["pallet"] = s.PalletNId })]),
                    ["exceptions"] = Codes(exceptions.Where(e => e.Group == ExceptionGroups.SkidBuild && e.OrderNId == line.OrderNId && e.Dock == line.Dock)),
                };
            })]),
        };
        var built = platform.SendMessage(new OutboundMessage(ShipAdapters.SkidBuild, "SkidBuild", skidBuild.ToJsonString()), Wait);
        if (!built.Delivered)
        {
            return ShipmentResponse.Fail(NvErrors.NotConfirmed, $"SkidBuild to CustomerShipSys failed: {built.Error}");
        }

        var automatic = platform.Query<LoadingExceptionCode>().Where(c => c.Automatic).Select(c => c.NId).ToList();
        var shipLoad = new JsonObject
        {
            ["delivery"] = delivery.NId,
            ["route"] = (exceptions.Any(e => automatic.Contains(e.CodeNId!)) ? "EX-" : string.Empty) + group.Route,
            ["trailer"] = trailer.TrailerNo,
            ["seal"] = seal,
            ["skids"] = skids.Count,
            ["exceptions"] = Codes(exceptions.Where(e => e.Group is ExceptionGroups.SkidLevel or ExceptionGroups.Trailer)),
        };
        var loaded = platform.SendMessage(new OutboundMessage(ShipAdapters.ShipLoad, "ShipLoad", shipLoad.ToJsonString()), Wait);
        if (!loaded.Delivered)
        {
            return ShipmentResponse.Fail(NvErrors.NotConfirmed, $"ShipLoad to CustomerShipSys failed: {loaded.Error}");
        }

        string? confirmation = null, trip = null;
        try
        {
            var reply = JsonNode.Parse(loaded.Content ?? string.Empty);
            confirmation = reply?["confirmationNo"]?.GetValue<string>();
            trip = reply?["tripNo"]?.GetValue<string>();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            platform.Tracer.Write(TraceLevel.Warning, $"ShipLoad reply is not JSON: {e.Message}");
        }

        if (string.IsNullOrWhiteSpace(confirmation) || string.IsNullOrWhiteSpace(trip))
        {
            return ShipmentResponse.Fail(NvErrors.NotConfirmed, $"CustomerShipSys replied without a confirmation and trip number: {loaded.Content}");
        }

        delivery.ConfirmationNo = confirmation;
        delivery.TripNo = trip;
        return null;
    }

    private static JsonArray Codes(IEnumerable<LoadingExceptionEntry> exceptions) =>
        new([.. exceptions.Select(e => (JsonNode)new JsonObject { ["code"] = e.CodeNId, ["comment"] = e.Comment })]);

    private static void BillOfLading(IPlatform platform, Delivery delivery, PickListGroup group, Trailer trailer)
    {
        var lines = Shipments.Lines(platform, group.NId);
        Shipments.Label(platform, "BillOfLadingTemplate", [new XElement("label",
            new XElement("billOfLading", delivery.BillOfLadingNo),
            new XElement("trailer", trailer.TrailerNo),
            new XElement("seal", delivery.SealNo),
            new XElement("shipTo", group.ShipTo),
            new XElement("route", group.Route),
            new XElement("pallets", platform.Query<Skid>().Count(s => s.DeliveryNId == delivery.NId && s.Status == SkidStates.Loaded)),
            new XElement("quantity", lines.Sum(l => l.LoadedQuantity).ToString("0", CultureInfo.InvariantCulture)),
            new XElement("orders", lines.Select(l => l.OrderNId).Distinct().Select(o => new XElement("order", o))))]);
    }
}

/// <summary>
/// Xe của hãng vận tải tới nhận hàng, đỗ ở một ô yard: gắn xe với chuyến giao, xe Free → CustomerEmpty. Route pool của xe phải trùng
/// nơi nhận của chuyến.
/// </summary>
[Command<YardOperationBlock>]
public sealed class ArriveCustomerTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? DeliveryNId { get; init; }

    [Mandatory]
    public string? LocationNId { get; init; }
}

internal sealed class ArriveCustomerTrailerHandler : ICommandHandler<ArriveCustomerTrailer, YardResponse>
{
    public YardResponse Handle(ArriveCustomerTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (trailer.Status != TrailerStates.Free || !string.IsNullOrEmpty(trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status} at {trailer.LocationNId}; it has not departed yet.");
        }

        var delivery = platform.Query<Delivery>().SingleOrDefault(d => d.NId == command.DeliveryNId);
        if (delivery is null || delivery.Status != DeliveryStates.New)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Delivery {command.DeliveryNId} does not exist or already has a trailer.");
        }

        var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
        if (trailer.RoutePool != group.ShipTo)
        {
            return YardResponse.Fail(NvErrors.WrongRoutePool, $"Trailer {trailer.NId} serves {trailer.RoutePool}; delivery {delivery.NId} goes to {group.ShipTo}.");
        }

        if (!platform.ProjectionQuery<EquipmentProperty>().Any(p => p.NId == command.LocationNId + "|LocationType" && p.Value == "Yard"))
        {
            return YardResponse.Fail(NvErrors.UnknownLocation, $"{command.LocationNId} is not a yard slot.");
        }

        if (platform.Query<Trailer>().Any(t => t.LocationNId == command.LocationNId))
        {
            return YardResponse.Fail(NvErrors.LocationBusy, $"Location {command.LocationNId} has a trailer already.");
        }

        trailer.LocationNId = command.LocationNId;
        Yard.SetStatus(platform, trailer, TrailerStates.CustomerEmpty);
        platform.Submit(trailer);
        delivery.TrailerNId = trailer.NId;
        delivery.Status = DeliveryStates.Arrived;
        platform.Submit(delivery);
        return YardResponse.Of(trailer);
    }
}

/// <summary>Move In: yêu cầu kéo xe của chuyến từ yard vào một cửa dock trống. Xe phải CustomerEmpty, route pool trùng nơi nhận.</summary>
[Command<YardOperationBlock>]
public sealed class MoveTrailerIn : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? DockNId { get; init; }

    public string? Priority { get; init; }
}

internal sealed class MoveTrailerInHandler : ICommandHandler<MoveTrailerIn, YardResponse>
{
    public YardResponse Handle(MoveTrailerIn command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (trailer.Status != TrailerStates.CustomerEmpty)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status}; Move In takes an empty customer trailer.");
        }

        var delivery = platform.Query<Delivery>().SingleOrDefault(d => d.TrailerNId == trailer.NId && d.Status == DeliveryStates.Arrived);
        var shipTo = delivery is null ? null : platform.Query<PickListGroup>().Where(g => g.NId == delivery.GroupNId).Select(g => g.ShipTo).Single();
        if (delivery is null || trailer.RoutePool != shipTo)
        {
            return YardResponse.Fail(NvErrors.WrongRoutePool, $"Trailer {trailer.NId} has no open delivery for its route pool {trailer.RoutePool}.");
        }

        if (!Yard.IsDock(platform, command.DockNId))
        {
            return YardResponse.Fail(NvErrors.TrailerNotAtDock, $"{command.DockNId} is not a dock.");
        }

        return Yard.Move(platform, trailer, command.DockNId!, command.Priority);
    }
}

/// <summary>Move Out: yêu cầu kéo xe đã chất từ cửa dock ra một ô yard của nhóm route chính của chuyến.</summary>
[Command<YardOperationBlock>]
public sealed class MoveTrailerOut : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? LocationNId { get; init; }

    public string? Priority { get; init; }
}

internal sealed class MoveTrailerOutHandler : ICommandHandler<MoveTrailerOut, YardResponse>
{
    public YardResponse Handle(MoveTrailerOut command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        var delivery = platform.Query<Delivery>().SingleOrDefault(d => d.TrailerNId == trailer.NId && d.Status != DeliveryStates.Departed);
        if (delivery is null || !Yard.IsDock(platform, trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.TrailerNotAtDock, $"Trailer {trailer.NId} is not at a dock for a delivery.");
        }

        var route = platform.Query<PickListGroup>().Where(g => g.NId == delivery.GroupNId).Select(g => g.Route).Single()!;
        var allowed = Shipments.RouteLocations(platform, route, "Yard");
        if (!allowed.Contains(command.LocationNId!))
        {
            return YardResponse.Fail(NvErrors.NoRouteArea,
                $"{command.LocationNId} is not a yard slot of route group {Shipments.MainRoute(route)} ({string.Join(", ", allowed)}).");
        }

        return Yard.Move(platform, trailer, command.LocationNId!, command.Priority);
    }
}

/// <summary>In yard pass cho xe đã chất: mã <c>YP-</c> + số đếm cơ số 36, gắn vào xe và chuyến.</summary>
[Command<YardOperationBlock>]
public sealed class IssueYardPass : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }
}

internal sealed class IssueYardPassHandler : ICommandHandler<IssueYardPass, YardResponse>
{
    public YardResponse Handle(IssueYardPass command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        var delivery = platform.Query<Delivery>().SingleOrDefault(d => d.TrailerNId == trailer.NId && d.Status == DeliveryStates.Loaded);
        if (delivery is null)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} has no loaded delivery.");
        }

        trailer.YardPassNId = "YP-" + Counters.Base36(Counters.Take(platform, "YardPass") + 1).PadLeft(6, '0');
        platform.Submit(trailer);
        delivery.YardPassNId = trailer.YardPassNId;
        platform.Submit(delivery);
        return new YardResponse { NId = trailer.YardPassNId, Status = trailer.Status };
    }
}

/// <summary>
/// Xe rời nhà máy (Trailer Departed, nhập yard pass). Xe không còn yêu cầu kéo đang mở, không đứng ở dock. Chuyến Departed; xe Free,
/// không vị trí, không yard pass; nhóm và đơn Shipped (tên nhóm thêm hậu tố); mỗi pallet: hàng Shipped, tote rời pallet về Empty,
/// pallet Empty số lượng 0 ở <c>n/a</c>. Phát <see cref="DeliveryDeparted"/>.
/// </summary>
[Command<YardOperationBlock>]
public sealed class DepartCustomerTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? YardPassNId { get; init; }
}

internal sealed class DepartCustomerTrailerHandler : ICommandHandler<DepartCustomerTrailer, YardResponse>
{
    public YardResponse Handle(DepartCustomerTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (platform.Query<TrailerMovement>().Any(m => m.TrailerNId == trailer.NId && m.Status != MovementStates.Dropped))
        {
            return YardResponse.Fail(NvErrors.MovementOpen, $"Trailer {trailer.NId} has an open movement request.");
        }

        if (Yard.IsDock(platform, trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.TrailerAtDock, $"Trailer {trailer.NId} is still at {trailer.LocationNId}; move it out first.");
        }

        if (string.IsNullOrEmpty(trailer.YardPassNId) || trailer.YardPassNId != command.YardPassNId)
        {
            return YardResponse.Fail(NvErrors.NoYardPass, $"Yard pass {command.YardPassNId} is not the yard pass of trailer {trailer.NId}.");
        }

        var delivery = platform.Query<Delivery>().Single(d => d.TrailerNId == trailer.NId && d.Status == DeliveryStates.Loaded);
        var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
        foreach (var picked in platform.Query<PickedPallet>().Where(p => p.GroupNId == group.NId && p.Status == PalletStates.Loaded).ToList())
        {
            if (ShipPallet(platform, picked.PalletNId!) is { } failed)
            {
                return YardResponse.From(failed);
            }

            picked.Status = OrderStates.Shipped;
            platform.Submit(picked);
        }

        foreach (var line in Shipments.Lines(platform, group.NId))
        {
            line.ShippedQuantity += line.LoadedQuantity;
            line.LoadedQuantity = 0;
            platform.Submit(line);
        }

        var now = platform.Time.GetUtcNow();
        Shipments.SetOrders(platform, group.NId, OrderStates.Shipped);
        group.Status = OrderStates.Shipped;
        group.Name += " Shipped " + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        platform.Submit(group);
        delivery.Status = DeliveryStates.Departed;
        delivery.DepartedOn = now;
        platform.Submit(delivery);
        trailer.LocationNId = null;
        trailer.YardPassNId = null;
        Yard.SetStatus(platform, trailer, TrailerStates.Free);
        platform.Submit(trailer);
        platform.RaiseEventOnCommit(new DeliveryDeparted(delivery.NId));
        return YardResponse.Of(trailer);
    }

    private static ProductResponse? ShipPallet(IPlatform platform, string pallet)
    {
        foreach (var tote in platform.Query<Mtua>().Where(c => c.ParentMtuaNId == pallet).Select(c => c.NId).ToList())
        {
            foreach (var item in platform.Query<Mtu>().Where(m => m.ParentMtuaNId == tote).Select(m => m.NId).ToList())
            {
                var shipped = platform.CallCommand<ShipMtu, ProductResponse>(new ShipMtu { NId = item });
                if (!shipped.Succeeded)
                {
                    return shipped;
                }
            }

            var emptied = PackingData.Recount(platform, tote);
            var off = emptied.Succeeded ? platform.CallCommand<RemoveFromMtua, ProductResponse>(new RemoveFromMtua { ChildNId = tote }) : emptied;
            if (!off.Succeeded)
            {
                return off;
            }
        }

        foreach (var item in platform.Query<Mtu>().Where(m => m.ParentMtuaNId == pallet).Select(m => m.NId).ToList())
        {
            var shipped = platform.CallCommand<ShipMtu, ProductResponse>(new ShipMtu { NId = item });
            if (!shipped.Succeeded)
            {
                return shipped;
            }
        }

        var recount = PackingData.Recount(platform, pallet);
        var moved = recount.Succeeded ? platform.CallCommand<MoveMtua, ProductResponse>(new MoveMtua { NId = pallet, EquipmentNId = EquipmentLevels.NotApplicable }) : recount;
        return moved.Succeeded ? null : moved;
    }
}

/// <summary>ASN cho nơi nhận họ A khi xe của chuyến rời: hãng vận tải, vận đơn, các tote và hàng, gửi bất đồng bộ ra file.</summary>
[EventSubscriber<ShipOperationBlock>(Subscription = "DeliveryAsn", Predefined = true, WorkerRole = "FinishedGoods")]
public sealed class AsnSender : IEventSubscriber<DeliveryDeparted>
{
    public void Handle(DeliveryDeparted domainEvent, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(platform);
        var delivery = platform.Query<Delivery>().Single(d => d.NId == domainEvent.DeliveryNId);
        var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
        if (Shipments.FamilyOf(platform, group.NId) != ShipFamilies.A)
        {
            return;
        }

        var orders = Shipments.Orders(platform, group.NId);
        var pallets = platform.Query<PickedPallet>().Where(p => p.GroupNId == group.NId && p.Status == OrderStates.Shipped).Select(p => p.PalletNId).ToList();
        var skids = platform.Query<Skid>().Where(s => s.DeliveryNId == delivery.NId && pallets.Contains(s.PalletNId)).Select(s => s.NId).ToList();
        var items = platform.Query<SkidContent>().Where(c => skids.Contains(c.SkidNId!)).Select(c => c.ItemNId).ToList();
        var history = platform.Query<MtuHistory>().Where(h => items.Contains(h.MtuNId) && h.Detail!.Contains(MtuStatus.Shipped)).ToList();
        var asn = new XElement("asn",
            new XElement("billOfLading", delivery.BillOfLadingNo),
            new XElement("carrier", orders.Select(o => o.CarrierCode).FirstOrDefault()),
            new XElement("shipTo", group.ShipTo),
            new XElement("shippedOn", delivery.DepartedOn?.ToString("O", CultureInfo.InvariantCulture)),
            new XElement("orders", orders.Select(o => new XElement("order", new XAttribute("id", o.NId), new XAttribute("reference", o.CustomerReference ?? string.Empty)))),
            new XElement("items", items.Select(i => new XElement("item", new XAttribute("tote", Tote(history, i)), i))));
        platform.SendMessageAsync(new OutboundMessage(ShipAdapters.Asn, "Asn", asn.ToString(), "application/xml"));

        var document = platform.Query<AsnDocument>().SingleOrDefault(a => a.NId == delivery.BillOfLadingNo) ?? platform.Create<AsnDocument>();
        document.NId = delivery.BillOfLadingNo!;
        document.DeliveryNId = delivery.NId;
        document.ShipTo = group.ShipTo;
        document.SentOn = platform.Time.GetUtcNow();
        platform.Submit(document);
    }

    // Tote của hàng lúc giao: ghi ở dòng lịch sử Shipped ("out of <tote>").
    private static string Tote(List<MtuHistory> history, string? item) =>
        history.Where(h => h.MtuNId == item).Select(h => h.Detail!).FirstOrDefault()?.Split("out of ").LastOrDefault() ?? string.Empty;
}

/// <summary>Phản hồi của khách cho một ASN (tìm theo số chứng từ): ghi vào ASN, email người nhận của nơi nhận.</summary>
[Command<ShipOperationBlock>]
public sealed class RecordAsnResponse : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? DocumentNo { get; init; }

    [Mandatory]
    public string? Status { get; init; }

    public string? Message { get; init; }
}

internal sealed class RecordAsnResponseHandler : ICommandHandler<RecordAsnResponse, ShipmentResponse>
{
    public ShipmentResponse Handle(RecordAsnResponse command, IPlatform platform)
    {
        var document = platform.Query<AsnDocument>().SingleOrDefault(a => a.NId == command.DocumentNo);
        if (document is null)
        {
            return ShipmentResponse.Fail(NvErrors.UnknownShipment, $"No ASN with document number {command.DocumentNo}.");
        }

        document.ResponseStatus = command.Status;
        document.ResponseMessage = command.Message;
        platform.Submit(document);
        string? warning = null;
        if (Shipments.ShipTo(platform, document.ShipTo)?.AsnRecipients is { Length: > 0 } recipients)
        {
            platform.SendMessageAsync(new OutboundMessage(ShipAdapters.Mail, "AsnResponse", new XElement("mail",
                new XElement("to", recipients), new XElement("subject", $"ASN {document.NId}: {command.Status}"),
                new XElement("body", command.Message)).ToString(), "application/xml"));
        }
        else
        {
            warning = $"No ASN recipients set for {document.ShipTo}.";
        }

        return new ShipmentResponse { NId = document.NId, Status = document.ResponseStatus, DeliveryNId = document.DeliveryNId, Warning = warning };
    }
}

/// <summary>Xe chọn được ở Move In của một chuyến: route pool trùng nơi nhận, CustomerEmpty, gắn chuyến đó, chưa có yêu cầu kéo đang mở.</summary>
[ReadingFunction(YardApp.Name)]
public sealed class DockableTrailers : IReadingFunction<DeliveryParameters, Trailer>
{
    public IQueryable<Trailer> Read(DeliveryParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var delivery = platform.ProjectionQuery<Delivery>().SingleOrDefault(d => d.NId == parameters.DeliveryNId);
        var shipTo = delivery is null ? null : platform.ProjectionQuery<PickListGroup>().Where(g => g.NId == delivery.GroupNId).Select(g => g.ShipTo).SingleOrDefault();
        var moving = platform.ProjectionQuery<TrailerMovement>().Where(m => m.Status != MovementStates.Dropped).Select(m => m.TrailerNId).ToList();
        return platform.ProjectionQuery<Trailer>().Where(t => t.RoutePool == shipTo && t.Status == TrailerStates.CustomerEmpty
            && t.NId == delivery!.TrailerNId && !moving.Contains(t.NId));
    }
}

public sealed class DeliveryParameters
{
    public string? DeliveryNId { get; init; }
}
