using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Xuất hàng, phần của team trên ShipApp: đơn bán từ ERP, nhóm đơn theo khách + route + giờ giao, picking theo FIFO, nhãn skid,
// staging. Chất xe, ngoại lệ, xe rời và ASN ở Loading.cs. Hai họ khách: họ A (kit, nhãn SkidTag in sẵn, đo cuối, ASN khi xe đi),
// họ B (module, dữ liệu đặt hàng CustomerBoxOrder, nhãn SkidSheet hai mặt và nhãn hộp, hệ CustomerShipSys xác nhận lúc chất xe).

public static class ShipFamilies
{
    public const string A = "A";
    public const string B = "B";
}

public static class OrderStates
{
    public const string New = "New";
    public const string Released = "Released";
    public const string Picked = "Picked";
    public const string Staging = "Staging";
    public const string Staged = "Staged";
    public const string Loading = "Loading";
    public const string Shipped = "Shipped";
}

public static class DeliveryStates
{
    public const string New = "New";
    public const string Arrived = "Arrived";
    public const string Loaded = "Loaded";
    public const string Departed = "Departed";
}

public static class SkidStates
{
    /// <summary>Họ A: bản ghi có từ lúc in nhãn, chưa quét.</summary>
    public const string Printed = "Printed";

    public const string Building = "Building";
    public const string Staged = "Staged";
    public const string Loaded = "Loaded";
}

/// <summary>Loại nhãn skid theo nơi nhận.</summary>
public static class SkidLabels
{
    /// <summary>Họ A: một nhãn mỗi skid, bản ghi skid tạo lúc in.</summary>
    public const string SkidTag = "SkidTag";

    /// <summary>Họ B: hai nhãn (mặt A, B) mỗi skid, bản ghi skid tạo lúc quét ở staging.</summary>
    public const string SkidSheet = "SkidSheet";
}

/// <summary>Adapter của tin xuất hàng; nơi chúng đi tới là cấu hình của tầng tích hợp.</summary>
public static class ShipAdapters
{
    public const string LabelPrinter = "LabelPrinter";
    public const string Mail = "Mail";
    public const string Asn = "AsnFile";
    public const string SkidBuild = "CustomerShipSys_SkidBuild";
    public const string ShipLoad = "CustomerShipSys_ShipLoad";
}

/// <summary>Cấu hình theo nơi nhận: họ, loại nhãn, mã nhà cung cấp của nhà máy, người nhận email.</summary>
[Entity<ShipOperationBlock>]
public sealed class ShipToConfig : Entity
{
    public string? Family { get; set; }

    public string? SkidLabel { get; set; }

    /// <summary>Nhãn hộp (họ B): in theo dữ liệu đặt hàng của khách.</summary>
    public bool BoxLabels { get; set; }

    public string? CarrierCode { get; set; }

    [TextLength(-1)]
    public string? StagedPalletRecipients { get; set; }

    [TextLength(-1)]
    public string? AsnRecipients { get; set; }
}

/// <summary>Đơn bán từ ERP. NId = số đơn ERP.</summary>
[Entity<ShipOperationBlock>]
public sealed class SalesOrder : Entity
{
    public string? CustomerReference { get; set; }

    [Mandatory]
    public string? ShipTo { get; set; }

    public string? Family { get; set; }

    public string? Route { get; set; }

    public DateTimeOffset? DeliverOn { get; set; }

    /// <summary>Mã hãng vận tải; có là đơn họ A.</summary>
    public string? CarrierCode { get; set; }

    public string? Status { get; set; }

    public string? GroupNId { get; set; }

    /// <summary>Mọi pallet đều lấy đúng pallet được đề xuất. Lấy lệch không bị chặn, chỉ hạ cờ này.</summary>
    public bool FifoPicked { get; set; } = true;
}

/// <summary>Dòng đơn. NId = <c>&lt;đơn&gt;-&lt;dòng&gt;</c>.</summary>
[Entity<ShipOperationBlock>]
public sealed class SalesOrderLine : Entity
{
    [Mandatory]
    public string? OrderNId { get; set; }

    [Mandatory]
    public string? MaterialNId { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Số hàng mỗi kiện của khách; số skid = số lượng chia số này, làm tròn lên.</summary>
    public decimal ContainerQuantity { get; set; }

    public string? CustomerOrderNo { get; set; }

    /// <summary>Dock nhận hàng của khách (không phải cửa dock của nhà máy).</summary>
    public string? Dock { get; set; }

    public string? SupplierCode { get; set; }

    public decimal PickedQuantity { get; set; }

    public decimal StagedQuantity { get; set; }

    public decimal LoadedQuantity { get; set; }

    public decimal ShippedQuantity { get; set; }
}

/// <summary>Nhóm đơn: các đơn cùng nơi nhận, route, giờ giao, chưa giao. Đi chung một delivery.</summary>
[Entity<ShipOperationBlock>]
public sealed class PickListGroup : Entity
{
    public string? Name { get; set; }

    public string? ShipTo { get; set; }

    public string? Route { get; set; }

    public DateTimeOffset? DeliverOn { get; set; }

    public string? Status { get; set; }

    public string? DeliveryNId { get; set; }
}

/// <summary>Chuyến giao của một nhóm đơn, kiêm lịch chất hàng ở dock: xe, số xác nhận của khách, vận đơn, yard pass.</summary>
[Entity<ShipOperationBlock>]
public sealed class Delivery : Entity
{
    [Mandatory]
    public string? GroupNId { get; set; }

    public string? Status { get; set; }

    public string? TrailerNId { get; set; }

    public string? ConfirmationNo { get; set; }

    public string? TripNo { get; set; }

    public string? BillOfLadingNo { get; set; }

    public string? SealNo { get; set; }

    public string? YardPassNId { get; set; }

    public DateTimeOffset? DepartedOn { get; set; }
}

/// <summary>Pallet đã lấy cho một nhóm đơn. NId = <c>&lt;nhóm&gt;|&lt;pallet&gt;</c>.</summary>
[Entity<ShipOperationBlock>]
public sealed class PickedPallet : Entity
{
    [Mandatory]
    public string? GroupNId { get; set; }

    [Mandatory]
    public string? PalletNId { get; set; }

    public string? OrderLineNId { get; set; }

    public string? SuggestedPalletNId { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Picked, Staged, Loaded, Shipped (theo pallet).</summary>
    public string? Status { get; set; }
}

/// <summary>Kiện giao: đúng một pallet. NId = mã trên nhãn (họ A); họ B: <c>&lt;chuyến&gt;|&lt;mã nhãn không gồm mặt&gt;</c>.</summary>
[Entity<ShipOperationBlock>]
public sealed class Skid : Entity
{
    [Mandatory]
    public string? DeliveryNId { get; set; }

    [Mandatory]
    public string? OrderLineNId { get; set; }

    public int SkidNo { get; set; }

    public string? PalletNId { get; set; }

    public string? Status { get; set; }
}

/// <summary>Hàng ghi vào skid: serial (họ A, tự ghi lúc staging) hoặc nhãn hộp đã quét (họ B). NId = <c>&lt;skid&gt;|&lt;mã&gt;</c>.</summary>
[Entity<ShipOperationBlock>]
public sealed class SkidContent : Entity
{
    [Mandatory]
    public string? SkidNId { get; set; }

    public string? ItemNId { get; set; }

    public string? CustomerOrderNo { get; set; }

    public int BoxNo { get; set; }
}

/// <summary>Dữ liệu đặt hàng của khách họ B, gửi trước đơn. NId = mã đơn của khách.</summary>
[Entity<ShipOperationBlock>]
public sealed class CustomerBoxOrder : Entity
{
    public string? ShipTo { get; set; }

    public string? Dock { get; set; }

    public string? SupplierCode { get; set; }

    /// <summary>Kanban của khách: mã họ dùng gọi part về line của họ, khác kanban nội bộ.</summary>
    public string? CustomerKanban { get; set; }

    public string? MaterialNId { get; set; }

    public decimal QuantityPerBox { get; set; }

    public int TotalBoxes { get; set; }

    public string? RouteCode { get; set; }
}

public sealed class ShipmentResponse : CommandResponse
{
    public string? NId { get; set; }

    public string? Status { get; set; }

    public string? GroupNId { get; set; }

    public string? DeliveryNId { get; set; }

    /// <summary>Pallet đứng đầu đề xuất lúc lấy; khác pallet vừa lấy là lấy lệch FIFO.</summary>
    public string? SuggestedPalletNId { get; set; }

    /// <summary>Skid còn phải dựng / đã xong của chuyến giao (màn staging, màn chất xe).</summary>
    public int Todo { get; set; }

    public int Completed { get; set; }

    public int Labels { get; set; }

    /// <summary>Việc không chặn lệnh nhưng người dùng cần biết (vd không gửi được email).</summary>
    public string? Warning { get; set; }

    public List<SalesOrderAck> Acks { get; set; } = [];

    internal static ShipmentResponse Fail(int code, string message)
    {
        var response = new ShipmentResponse();
        response.SetError(code, message);
        return response;
    }

    internal static ShipmentResponse From(CommandResponse failed) => Fail(failed.Error!.ErrorCode, failed.Error.ErrorMessage);
}

/// <summary>Ack của một đơn trong payload: nhận hay không, lý do.</summary>
public sealed class SalesOrderAck
{
    public string? OrderNo { get; set; }

    public bool Accepted { get; set; }

    public string? Message { get; set; }

    public string? GroupNId { get; set; }
}

internal static class Shipments
{
    public static ShipmentResponse? Group(IPlatform platform, string nid, out PickListGroup group)
    {
        group = platform.Query<PickListGroup>().SingleOrDefault(g => g.NId == nid)!;
        return group is null ? ShipmentResponse.Fail(NvErrors.UnknownShipment, $"Pick list group {nid} does not exist.") : null;
    }

    public static ShipmentResponse? Delivery(IPlatform platform, string nid, out Delivery delivery)
    {
        delivery = platform.Query<Delivery>().SingleOrDefault(d => d.NId == nid)!;
        return delivery is null ? ShipmentResponse.Fail(NvErrors.UnknownShipment, $"Delivery {nid} does not exist.") : null;
    }

    public static List<SalesOrder> Orders(IPlatform platform, string group) => [.. platform.Query<SalesOrder>().Where(o => o.GroupNId == group).OrderBy(o => o.NId)];

    public static List<SalesOrderLine> Lines(IPlatform platform, string group)
    {
        var orders = platform.Query<SalesOrder>().Where(o => o.GroupNId == group).Select(o => o.NId).ToList();
        return [.. platform.Query<SalesOrderLine>().Where(l => orders.Contains(l.OrderNId!)).OrderBy(l => l.NId)];
    }

    public static string FamilyOf(IPlatform platform, string group) =>
        platform.Query<SalesOrder>().Where(o => o.GroupNId == group).Select(o => o.Family).FirstOrDefault() ?? ShipFamilies.A;

    public static void SetOrders(IPlatform platform, string group, string status, Func<SalesOrder, bool>? when = null)
    {
        foreach (var order in Orders(platform, group).Where(o => o.Status != status && (when?.Invoke(o) ?? true)))
        {
            order.Status = status;
            platform.Submit(order);
        }
    }

    /// <summary>Route chính: route từ 10 ký tự trở lên lấy 3 ký tự đầu; route ngắn hơn bỏ 2 ký tự cuối. Tên nhóm equipment của route.</summary>
    public static string MainRoute(string route) => route.Length >= 10 ? route[..3] : route.Length > 2 ? route[..^2] : route;

    /// <summary>Equipment của nhóm route có property <c>LocationType</c> = <paramref name="type"/>.</summary>
    public static List<string> RouteLocations(IReadOnlyPlatform platform, string route, string type)
    {
        var main = MainRoute(route);
        var members = platform.ProjectionQuery<EquipmentGroupMember>().Where(m => m.GroupNId == main && m.GroupType == "Route")
            .Select(m => m.EquipmentNId!).ToList();
        var typed = platform.ProjectionQuery<EquipmentProperty>().Where(p => p.Name == "LocationType" && p.Value == type)
            .Select(p => p.EquipmentNId!).ToList();
        return [.. members.Where(typed.Contains).Order(StringComparer.Ordinal)];
    }

    public static ShipToConfig? ShipTo(IPlatform platform, string? shipTo) => platform.Query<ShipToConfig>().SingleOrDefault(c => c.NId == shipTo);

    public static string Counter(IPlatform platform, string prefix, string name, int width) =>
        prefix + (Counters.Take(platform, name) + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    /// <summary>Tiến độ staging của chuyến: skid phải dựng = số skid kế hoạch còn chưa xong; họ B chỉ đếm skid đã có pallet được lấy.</summary>
    public static (int Todo, int Completed) Progress(IPlatform platform, Delivery delivery)
    {
        var skids = platform.Query<Skid>().Where(s => s.DeliveryNId == delivery.NId).ToList();
        var completed = skids.Count(s => s.Status is SkidStates.Staged or SkidStates.Loaded);
        var picked = platform.Query<PickedPallet>().Count(p => p.GroupNId == delivery.GroupNId);
        return (Math.Max(picked - completed, 0), completed);
    }

    public static void Label(IPlatform platform, string template, IEnumerable<XElement> labels) =>
        platform.SendMessageAsync(new OutboundMessage(ShipAdapters.LabelPrinter, template,
            new XDocument(new XElement("labels", labels)).ToString(), "application/xml"));
}

/// <summary>Mã vạch của họ B. Bố cục riêng của NovaVolt, độ dài cố định; mã thật của khách không dùng ở đây.</summary>
internal static class ShipBarcodes
{
    /// <summary>Route 8, mã đơn khách 10, dock 2, nhà cung cấp 5, nơi nhận 4, số skid 3: khóa của skid, 32 ký tự.</summary>
    public const int SkidKeyLength = 32;

    /// <summary>Khóa 32 ký tự, mặt (A/B) ở ký tự 33, hai số 0 đệm: 35 ký tự.</summary>
    public const int SkidSheetLength = 35;

    /// <summary>Nhãn hộp: nhà cung cấp 5, dock 2, kanban 4, part 16, mã đơn khách 10, số hộp 3, tổng hộp 3, đệm tới 60.</summary>
    public const int BoxLabelLength = 60;

    public static string SkidKey(SalesOrderLine line, string route, string shipTo, int skidNo) =>
        Fit(route, 8) + Fit(line.CustomerOrderNo, 10) + Fit(line.Dock, 2) + Fit(line.SupplierCode, 5) + Fit(shipTo, 4)
        + skidNo.ToString("000", CultureInfo.InvariantCulture);

    public static string SkidSheet(string key, char face) => key + face + "00";

    public static (string Route, string CustomerOrder, string Dock, string Supplier, string ShipTo, int SkidNo)? ParseSkid(string barcode) =>
        barcode.Length == SkidSheetLength && barcode[SkidKeyLength] is 'A' or 'B'
        && int.TryParse(barcode.AsSpan(29, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var no)
            ? (barcode[..8].Trim(), barcode[8..18].Trim(), barcode[18..20].Trim(), barcode[20..25].Trim(), barcode[25..29].Trim(), no)
            : null;

    public static string Box(CustomerBoxOrder order, int boxNo) =>
        (Fit(order.SupplierCode, 5) + Fit(order.Dock, 2) + Fit(order.CustomerKanban, 4) + Fit(order.MaterialNId, 16) + Fit(order.NId, 10)
            + boxNo.ToString("000", CultureInfo.InvariantCulture) + order.TotalBoxes.ToString("000", CultureInfo.InvariantCulture))
        .PadRight(BoxLabelLength, '0');

    public static (string Supplier, string Dock, string Kanban, string Material, string CustomerOrder, int BoxNo, int TotalBoxes)? ParseBox(string barcode) =>
        barcode.Length == BoxLabelLength
        && int.TryParse(barcode.AsSpan(37, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var box)
        && int.TryParse(barcode.AsSpan(40, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            ? (barcode[..5].Trim(), barcode[5..7].Trim(), barcode[7..11].Trim(), barcode[11..27].Trim(), barcode[27..37].Trim(), box, total)
            : null;

    private static string Fit(string? value, int width) => (value ?? string.Empty).PadRight(width)[..width];
}

/// <summary>
/// Nhận đơn bán từ ERP (payload JSON, một hoặc nhiều đơn), trả Ack từng đơn. Đơn thiếu trường bắt buộc, trùng số, hay họ B
/// không tìm được route thì Ack lỗi và không ghi; đơn khác trong cùng payload vẫn ghi. Họ theo mã hãng vận tải của đơn: có là họ
/// A (route = 3 ký tự nơi nhận + <c>yyMMddHHmm</c> của giờ giao), trống là họ B (route của khách trong đơn, trống thì tra dữ liệu
/// đặt hàng). Đơn vào nhóm cùng nơi nhận, route, giờ giao chưa giao; không có thì tạo nhóm mới kèm delivery.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class ReceiveSalesOrders : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? Payload { get; init; }
}

internal sealed class ReceiveSalesOrdersHandler : ICommandHandler<ReceiveSalesOrders, ShipmentResponse>
{
    public ShipmentResponse Handle(ReceiveSalesOrders command, IPlatform platform)
    {
        ErpOrders? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ErpOrders>(command.Payload!, Json);
        }
        catch (JsonException e)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidSalesOrder, $"Payload is not a sales order document: {e.Message}");
        }

        var response = new ShipmentResponse();
        foreach (var order in payload?.Orders ?? [])
        {
            var ack = Receive(platform, order);
            response.Acks.Add(ack);
            platform.Tracer.Write(ack.Accepted ? TraceLevel.Information : TraceLevel.Warning,
                $"Sales order {ack.OrderNo}: {(ack.Accepted ? "accepted into " + ack.GroupNId : ack.Message)}");
        }

        return response;
    }

    private static SalesOrderAck Receive(IPlatform platform, ErpOrder order)
    {
        var ack = new SalesOrderAck { OrderNo = order.OrderNo };
        var missing = new[] { (nameof(order.OrderNo), order.OrderNo), (nameof(order.ShipTo), order.ShipTo), (nameof(order.DeliveryDate), order.DeliveryDate),
            (nameof(order.DeliveryTime), order.DeliveryTime) }.Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => f.Item1).ToList();
        if (order.Items is not { Count: > 0 })
        {
            missing.Add(nameof(order.Items));
        }

        missing.AddRange((order.Items ?? []).SelectMany((i, n) => new[] { (nameof(i.Item), i.Item), (nameof(i.Material), i.Material) }
            .Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => $"Items[{n}].{f.Item1}")));
        missing.AddRange((order.Items ?? []).Select((i, n) => (i, n)).Where(x => x.i.Quantity is not > 0).Select(x => $"Items[{x.n}].Quantity"));
        if (missing.Count > 0)
        {
            return Reject(ack, $"Missing field(s): {string.Join(", ", missing)}.");
        }

        if (platform.Query<SalesOrder>().Any(o => o.NId == order.OrderNo))
        {
            return Reject(ack, $"Sales order {order.OrderNo} already exists.");
        }

        if (!DateTime.TryParseExact(order.DeliveryDate + order.DeliveryTime, "yyyy-MM-ddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return Reject(ack, $"Delivery date and time {order.DeliveryDate} {order.DeliveryTime} are not yyyy-MM-dd HHmm.");
        }

        var deliverOn = new DateTimeOffset(at, TimeSpan.Zero);
        var family = string.IsNullOrWhiteSpace(order.CarrierCode) ? ShipFamilies.B : ShipFamilies.A;
        string? route;
        if (family == ShipFamilies.A)
        {
            route = order.ShipTo!.PadRight(3)[..3] + deliverOn.ToString("yyMMddHHmm", CultureInfo.InvariantCulture);
        }
        else
        {
            var first = order.Items![0];
            route = order.CustomerRoute;
            if (string.IsNullOrWhiteSpace(route))
            {
                route = platform.Query<CustomerBoxOrder>()
                    .Where(b => b.NId == first.CustomerOrderNo && b.SupplierCode == first.SupplierCode && b.Dock == first.Dock)
                    .Select(b => b.RouteCode).FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(route))
            {
                return Reject(ack, $"No route: the order has no customer route and no box order {first.CustomerOrderNo} for supplier {first.SupplierCode}, dock {first.Dock}.");
            }
        }

        foreach (var item in order.Items!.Where(i => !platform.ProjectionQuery<Material>().Any(m => m.NId == i.Material)))
        {
            return Reject(ack, $"Material {item.Material} does not exist.");
        }

        var group = platform.Query<PickListGroup>()
            .Where(g => g.ShipTo == order.ShipTo && g.Route == route && g.DeliverOn == deliverOn && g.Status != OrderStates.Shipped)
            .OrderBy(g => g.CreatedOn).FirstOrDefault() ?? NewGroup(platform, order.ShipTo!, route!, deliverOn);

        var created = platform.Create<SalesOrder>();
        created.NId = order.OrderNo!;
        created.CustomerReference = order.CustomerReference;
        created.ShipTo = order.ShipTo;
        created.Family = family;
        created.Route = route;
        created.DeliverOn = deliverOn;
        created.CarrierCode = order.CarrierCode;
        created.Status = OrderStates.New;
        created.GroupNId = group.NId;
        platform.Submit(created);
        foreach (var item in order.Items!)
        {
            var line = platform.Create<SalesOrderLine>();
            line.NId = $"{order.OrderNo}-{item.Item}";
            line.OrderNId = order.OrderNo;
            line.MaterialNId = item.Material;
            line.Quantity = item.Quantity!.Value;
            line.ContainerQuantity = item.ContainerQuantity is > 0 ? item.ContainerQuantity.Value : item.Quantity.Value;
            line.CustomerOrderNo = item.CustomerOrderNo;
            line.Dock = item.Dock;
            line.SupplierCode = item.SupplierCode;
            platform.Submit(line);
        }

        // Nhóm đang làm dở nhận thêm đơn mới: đơn mới đi theo trạng thái của nhóm từ bước Release.
        if (group.Status != OrderStates.New)
        {
            created.Status = OrderStates.Released;
            platform.Submit(created);
        }

        ack.Accepted = true;
        ack.GroupNId = group.NId;
        return ack;
    }

    private static PickListGroup NewGroup(IPlatform platform, string shipTo, string route, DateTimeOffset deliverOn)
    {
        var group = platform.Create<PickListGroup>();
        group.NId = Shipments.Counter(platform, "PG", "PickListGroup", 6);
        group.Name = $"{shipTo} {route} {deliverOn.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}";
        group.ShipTo = shipTo;
        group.Route = route;
        group.DeliverOn = deliverOn;
        group.Status = OrderStates.New;
        var delivery = platform.Create<Delivery>();
        delivery.NId = Shipments.Counter(platform, "DL", "Delivery", 6);
        delivery.GroupNId = group.NId;
        delivery.Status = DeliveryStates.New;
        platform.Submit(delivery);
        group.DeliveryNId = delivery.NId;
        platform.Submit(group);
        return group;
    }

    private static SalesOrderAck Reject(SalesOrderAck ack, string message)
    {
        ack.Accepted = false;
        ack.Message = message;
        return ack;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed class ErpOrders
    {
        public List<ErpOrder>? Orders { get; set; }
    }

    private sealed class ErpOrder
    {
        public string? OrderNo { get; set; }

        public string? CustomerReference { get; set; }

        public string? ShipTo { get; set; }

        public string? DeliveryDate { get; set; }

        public string? DeliveryTime { get; set; }

        public string? CarrierCode { get; set; }

        public string? CustomerRoute { get; set; }

        public List<ErpItem>? Items { get; set; }
    }

    private sealed class ErpItem
    {
        public string? Item { get; set; }

        public string? Material { get; set; }

        public decimal? Quantity { get; set; }

        public decimal? ContainerQuantity { get; set; }

        public string? CustomerOrderNo { get; set; }

        public string? Dock { get; set; }

        public string? SupplierCode { get; set; }
    }
}

/// <summary>Ghi dữ liệu đặt hàng của khách họ B (theo mã đơn của khách); gửi lại cùng mã thì ghi đè.</summary>
[Command<ShipOperationBlock>]
public sealed class SetCustomerBoxOrder : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? CustomerOrderNo { get; init; }

    [Mandatory]
    public string? ShipTo { get; init; }

    [Mandatory]
    public string? Dock { get; init; }

    [Mandatory]
    public string? SupplierCode { get; init; }

    public string? CustomerKanban { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    [Mandatory]
    public decimal? QuantityPerBox { get; init; }

    [Mandatory]
    public int? TotalBoxes { get; init; }

    public string? RouteCode { get; init; }
}

internal sealed class SetCustomerBoxOrderHandler : ICommandHandler<SetCustomerBoxOrder, ShipmentResponse>
{
    public ShipmentResponse Handle(SetCustomerBoxOrder command, IPlatform platform)
    {
        if (command.QuantityPerBox is not > 0 || command.TotalBoxes is not > 0)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidSalesOrder, "Quantity per box and total boxes must be positive.");
        }

        var order = platform.Query<CustomerBoxOrder>().SingleOrDefault(b => b.NId == command.CustomerOrderNo) ?? platform.Create<CustomerBoxOrder>();
        order.NId = command.CustomerOrderNo!;
        order.ShipTo = command.ShipTo;
        order.Dock = command.Dock;
        order.SupplierCode = command.SupplierCode;
        order.CustomerKanban = command.CustomerKanban;
        order.MaterialNId = command.MaterialNId;
        order.QuantityPerBox = command.QuantityPerBox!.Value;
        order.TotalBoxes = command.TotalBoxes!.Value;
        order.RouteCode = command.RouteCode;
        platform.Submit(order);
        return new ShipmentResponse { NId = order.NId };
    }
}

/// <summary>Cấu hình một nơi nhận (master data).</summary>
[Command<ShipOperationBlock>]
public sealed class SetShipToConfig : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? ShipTo { get; init; }

    [Mandatory]
    public string? Family { get; init; }

    [Mandatory]
    public string? SkidLabel { get; init; }

    public bool BoxLabels { get; init; }

    public string? CarrierCode { get; init; }

    public string? StagedPalletRecipients { get; init; }

    public string? AsnRecipients { get; init; }
}

internal sealed class SetShipToConfigHandler : ICommandHandler<SetShipToConfig, ShipmentResponse>
{
    public ShipmentResponse Handle(SetShipToConfig command, IPlatform platform)
    {
        if (command.Family is not (ShipFamilies.A or ShipFamilies.B) || command.SkidLabel is not (SkidLabels.SkidTag or SkidLabels.SkidSheet))
        {
            return ShipmentResponse.Fail(NvErrors.InvalidSalesOrder, $"Family {command.Family} or skid label {command.SkidLabel} is not known.");
        }

        var config = platform.Query<ShipToConfig>().SingleOrDefault(c => c.NId == command.ShipTo) ?? platform.Create<ShipToConfig>();
        config.NId = command.ShipTo!;
        config.Family = command.Family;
        config.SkidLabel = command.SkidLabel;
        config.BoxLabels = command.BoxLabels;
        config.CarrierCode = command.CarrierCode;
        config.StagedPalletRecipients = command.StagedPalletRecipients;
        config.AsnRecipients = command.AsnRecipients;
        platform.Submit(config);
        return new ShipmentResponse { NId = config.NId };
    }
}

/// <summary>Release nhóm đơn: nhóm và các đơn sang Released, người ở kho thấy việc ở màn Picking.</summary>
[Command<ShipOperationBlock>]
public sealed class ReleasePickListGroup : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? GroupNId { get; init; }
}

internal sealed class ReleasePickListGroupHandler : ICommandHandler<ReleasePickListGroup, ShipmentResponse>
{
    public ShipmentResponse Handle(ReleasePickListGroup command, IPlatform platform)
    {
        if (Shipments.Group(platform, command.GroupNId!, out var group) is { } missing)
        {
            return missing;
        }

        if (group.Status != OrderStates.New)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidShipmentState, $"Pick list group {group.NId} is {group.Status}; only a new group is released.");
        }

        group.Status = OrderStates.Released;
        platform.Submit(group);
        Shipments.SetOrders(platform, group.NId, OrderStates.Released);
        return new ShipmentResponse { NId = group.NId, Status = group.Status, DeliveryNId = group.DeliveryNId };
    }
}

/// <summary>
/// Lấy một pallet cho nhóm đơn (màn Picking): pallet Full, có hàng, nằm ở ô kho có bản ghi FIFO, đúng part của một dòng còn thiếu;
/// không có hàng bị giữ hay hủy trong cây (procedure); họ A: đã qua đo cuối nếu part yêu cầu; họ B: hàng làm ra đủ số giờ theo key
/// <see cref="NvKeys.MinimumItemAgeHours"/>. Pallet sang Picked, vị trí giữ nguyên ô kho. Lấy khác pallet đứng đầu đề xuất thì
/// không chặn, chỉ hạ cờ FIFO của đơn.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class PickPallet : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? GroupNId { get; init; }

    [Mandatory]
    public string? PalletNId { get; init; }
}

internal sealed class PickPalletHandler : ICommandHandler<PickPallet, ShipmentResponse>
{
    public ShipmentResponse Handle(PickPallet command, IPlatform platform)
    {
        if (Shipments.Group(platform, command.GroupNId!, out var group) is { } missing)
        {
            return missing;
        }

        if (group.Status != OrderStates.Released)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidShipmentState, $"Pick list group {group.NId} is {group.Status}; picking needs a released group.");
        }

        var pallet = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.PalletNId && c.ContainerType == ContainerTypes.Pallet);
        if (pallet is null)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidPallet, $"Pallet {command.PalletNId} does not exist.");
        }

        if (pallet.Status != PalletStates.Full || pallet.Quantity <= 0)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidPallet, $"Pallet {pallet.NId} is {pallet.Status} with quantity {pallet.Quantity}; only a full pallet is picked.");
        }

        var fifo = platform.Query<FifoPallet>().SingleOrDefault(f => f.NId == pallet.NId);
        var location = MtuLocations.EquipmentOf(platform.Query<Mtu>(), platform.Query<Mtua>(), pallet.NId);
        if (fifo is null || fifo.LocationNId != location)
        {
            return ShipmentResponse.Fail(NvErrors.NotStored, $"Pallet {pallet.NId} has no FIFO record at {location}; store it first.");
        }

        var line = Shipments.Lines(platform, group.NId).FirstOrDefault(l => l.MaterialNId == pallet.MaterialNId && l.PickedQuantity < l.Quantity);
        if (line is null)
        {
            return ShipmentResponse.Fail(NvErrors.MaterialMismatch, $"Pick list group {group.NId} needs no more {pallet.MaterialNId}.");
        }

        var connection = FinishedGoodsSql.Connection(platform.ProjectionQuery<ConfigurationKey>);
        if (FinishedGoodsSql.Held(connection, pallet.NId) is { Count: > 0 } held)
        {
            return ShipmentResponse.Fail(NvErrors.HeldOrScrapped, $"Pallet {pallet.NId} holds {held.Count} item(s) on hold or scrapped.");
        }

        var order = platform.Query<SalesOrder>().Single(o => o.NId == line.OrderNId);
        if (order.Family == ShipFamilies.A && FinishedGoods.UntestedItem(platform, pallet) is { } untested)
        {
            return ShipmentResponse.Fail(NvErrors.NotTested, $"Pallet {pallet.NId}: {untested} has no passing end-of-line test.");
        }

        if (order.Family == ShipFamilies.B && TooYoung(platform, pallet) is { } young)
        {
            return ShipmentResponse.Fail(NvErrors.ItemTooYoung, young);
        }

        var suggested = FinishedGoodsSql.Suggested(connection, pallet.MaterialNId!).Select(s => s.PalletNId).FirstOrDefault();
        if (suggested != pallet.NId && order.FifoPicked)
        {
            order.FifoPicked = false;
            platform.Submit(order);
            platform.Tracer.Write(TraceLevel.Information, $"Order {order.NId}: picked {pallet.NId} while {suggested} was suggested first.");
        }

        var picked = platform.CallCommand<SetMtuaStatus, ProductResponse>(new SetMtuaStatus { NId = pallet.NId, Status = PalletStates.Picked });
        if (!picked.Succeeded)
        {
            return ShipmentResponse.From(picked);
        }

        fifo.Status = PalletStates.Picked;
        platform.Submit(fifo);
        var record = platform.Create<PickedPallet>();
        record.NId = group.NId + "|" + pallet.NId;
        record.GroupNId = group.NId;
        record.PalletNId = pallet.NId;
        record.OrderLineNId = line.NId;
        record.SuggestedPalletNId = suggested;
        record.Quantity = pallet.Quantity;
        record.Status = PalletStates.Picked;
        platform.Submit(record);
        line.PickedQuantity += pallet.Quantity;
        platform.Submit(line);
        return new ShipmentResponse { NId = pallet.NId, Status = PalletStates.Picked, GroupNId = group.NId, SuggestedPalletNId = suggested };
    }

    private static string? TooYoung(IPlatform platform, Mtua pallet)
    {
        var key = platform.ProjectionQuery<ConfigurationKey>().Where(k => k.NId == NvKeys.MinimumItemAgeHours).Select(k => k.Val).SingleOrDefault();
        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
        {
            return null;
        }

        var limit = platform.Time.GetUtcNow().AddHours(-hours);
        var young = PackingData.ItemsIn(platform, pallet.NId).Where(m => m.CreatedOn > limit).Select(m => m.NId).FirstOrDefault();
        return young is null ? null : $"Pallet {pallet.NId}: {young} was made less than {hours} hour(s) ago.";
    }
}

/// <summary>Bỏ pick một pallet chưa staging: pallet về Full, bản ghi FIFO về Full, dòng đơn trừ lại.</summary>
[Command<ShipOperationBlock>]
public sealed class UnpickPallet : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? GroupNId { get; init; }

    [Mandatory]
    public string? PalletNId { get; init; }
}

internal sealed class UnpickPalletHandler : ICommandHandler<UnpickPallet, ShipmentResponse>
{
    public ShipmentResponse Handle(UnpickPallet command, IPlatform platform)
    {
        var nid = command.GroupNId + "|" + command.PalletNId;
        var record = platform.Query<PickedPallet>().SingleOrDefault(p => p.NId == nid);
        if (record is null || record.Status != PalletStates.Picked)
        {
            return ShipmentResponse.Fail(NvErrors.NotPicked, $"Pallet {command.PalletNId} is not picked (and not yet staged) for {command.GroupNId}.");
        }

        var full = platform.CallCommand<SetMtuaStatus, ProductResponse>(new SetMtuaStatus { NId = record.PalletNId, Status = PalletStates.Full });
        if (!full.Succeeded)
        {
            return ShipmentResponse.From(full);
        }

        if (platform.Query<FifoPallet>().SingleOrDefault(f => f.NId == record.PalletNId) is { } fifo)
        {
            fifo.Status = PalletStates.Full;
            platform.Submit(fifo);
        }

        var line = platform.Query<SalesOrderLine>().Single(l => l.NId == record.OrderLineNId);
        line.PickedQuantity -= record.Quantity;
        platform.Submit(line);
        platform.Delete(record);
        return new ShipmentResponse { NId = command.PalletNId, Status = PalletStates.Full, GroupNId = command.GroupNId };
    }
}

/// <summary>
/// Đóng pick list: nhóm và đơn sang Picked. Lấy chưa đủ thì phải xác nhận (<see cref="AcceptShort"/>), như hộp thoại của màn; chưa
/// lấy pallet nào thì từ chối.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class CompletePickList : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? GroupNId { get; init; }

    public bool AcceptShort { get; init; }
}

internal sealed class CompletePickListHandler : ICommandHandler<CompletePickList, ShipmentResponse>
{
    public ShipmentResponse Handle(CompletePickList command, IPlatform platform)
    {
        if (Shipments.Group(platform, command.GroupNId!, out var group) is { } missing)
        {
            return missing;
        }

        if (group.Status != OrderStates.Released)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidShipmentState, $"Pick list group {group.NId} is {group.Status}.");
        }

        var lines = Shipments.Lines(platform, group.NId);
        var (picked, needed) = (lines.Sum(l => l.PickedQuantity), lines.Sum(l => l.Quantity));
        if (picked == 0)
        {
            return ShipmentResponse.Fail(NvErrors.NotPicked, $"Pick list group {group.NId} has no picked pallet.");
        }

        if (picked < needed && !command.AcceptShort)
        {
            return ShipmentResponse.Fail(NvErrors.ShortPick, $"Picked {picked} of {needed}; confirm to close the pick list short.");
        }

        group.Status = OrderStates.Picked;
        platform.Submit(group);
        Shipments.SetOrders(platform, group.NId, OrderStates.Picked);
        return new ShipmentResponse { NId = group.NId, Status = group.Status, DeliveryNId = group.DeliveryNId };
    }
}

/// <summary>
/// In nhãn skid của nhóm đơn, ra file theo mẫu qua adapter máy in. Số skid của dòng = số lượng chia số hàng mỗi kiện, làm tròn lên.
/// Họ A (<see cref="SkidLabels.SkidTag"/>): tạo bản ghi skid lúc in (in lại thì in đúng các skid đã có). Họ B
/// (<see cref="SkidLabels.SkidSheet"/>): hai nhãn mặt A, B mỗi skid, chưa có bản ghi.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class PrintSkidLabels : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? GroupNId { get; init; }
}

internal sealed class PrintSkidLabelsHandler : ICommandHandler<PrintSkidLabels, ShipmentResponse>
{
    public ShipmentResponse Handle(PrintSkidLabels command, IPlatform platform)
    {
        if (Shipments.Group(platform, command.GroupNId!, out var group) is { } missing)
        {
            return missing;
        }

        if (Shipments.ShipTo(platform, group.ShipTo) is not { } config)
        {
            return ShipmentResponse.Fail(NvErrors.NoLabelConfig, $"Customer labels are not set up for {group.ShipTo}.");
        }

        var labels = new List<XElement>();
        foreach (var line in Shipments.Lines(platform, group.NId))
        {
            var order = platform.Query<SalesOrder>().Single(o => o.NId == line.OrderNId);
            var count = (int)Math.Ceiling(line.Quantity / line.ContainerQuantity);
            for (var no = 1; no <= count; no++)
            {
                if (config.SkidLabel == SkidLabels.SkidTag)
                {
                    var skid = platform.Query<Skid>().SingleOrDefault(s => s.OrderLineNId == line.NId && s.SkidNo == no) ?? NewSkid(platform, group, line, no);
                    labels.Add(Label(skid.NId, no.ToString("000", CultureInfo.InvariantCulture), line, order, group));
                }
                else
                {
                    var key = ShipBarcodes.SkidKey(line, group.Route!, group.ShipTo!, no);
                    foreach (var face in "AB")
                    {
                        labels.Add(Label(ShipBarcodes.SkidSheet(key, face), no.ToString("000", CultureInfo.InvariantCulture) + face, line, order, group));
                    }
                }
            }
        }

        Shipments.Label(platform, config.SkidLabel + "Template", labels);
        return new ShipmentResponse { NId = group.NId, Labels = labels.Count };
    }

    private static Skid NewSkid(IPlatform platform, PickListGroup group, SalesOrderLine line, int no)
    {
        var skid = platform.Create<Skid>();
        skid.NId = "5T" + (line.SupplierCode ?? string.Empty) + (Counters.Take(platform, "SkidTag") + 1).ToString("0000000", CultureInfo.InvariantCulture);
        skid.DeliveryNId = group.DeliveryNId;
        skid.OrderLineNId = line.NId;
        skid.SkidNo = no;
        skid.Status = SkidStates.Printed;
        platform.Submit(skid);
        return skid;
    }

    private static XElement Label(string barcode, string skidId, SalesOrderLine line, SalesOrder order, PickListGroup group) =>
        new("label",
            new XElement("skidId", skidId),
            new XElement("barCode", barcode),
            new XElement("orderNo", line.CustomerOrderNo ?? order.CustomerReference),
            new XElement("salesOrder", order.NId),
            new XElement("part", line.MaterialNId),
            new XElement("quantity", line.ContainerQuantity.ToString("0", CultureInfo.InvariantCulture)),
            new XElement("shipTo", group.ShipTo),
            new XElement("dock", line.Dock),
            new XElement("route", group.Route));
}

/// <summary>In nhãn hộp của một dòng đơn họ B: số nhãn = tổng số hộp trong dữ liệu đặt hàng khớp mã đơn khách, nhà cung cấp, dock, part.</summary>
[Command<ShipOperationBlock>]
public sealed class PrintBoxLabels : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? OrderLineNId { get; init; }
}

internal sealed class PrintBoxLabelsHandler : ICommandHandler<PrintBoxLabels, ShipmentResponse>
{
    public ShipmentResponse Handle(PrintBoxLabels command, IPlatform platform)
    {
        var line = platform.Query<SalesOrderLine>().SingleOrDefault(l => l.NId == command.OrderLineNId);
        if (line is null)
        {
            return ShipmentResponse.Fail(NvErrors.UnknownShipment, $"Order line {command.OrderLineNId} does not exist.");
        }

        var box = platform.Query<CustomerBoxOrder>().SingleOrDefault(b => b.NId == line.CustomerOrderNo && b.SupplierCode == line.SupplierCode
            && b.Dock == line.Dock && b.MaterialNId == line.MaterialNId);
        if (box is null)
        {
            return ShipmentResponse.Fail(NvErrors.NoBoxOrder, $"No box order {line.CustomerOrderNo} for {line.MaterialNId}, supplier {line.SupplierCode}, dock {line.Dock}.");
        }

        var labels = Enumerable.Range(1, box.TotalBoxes).Select(n => new XElement("label",
            new XElement("barCode", ShipBarcodes.Box(box, n)),
            new XElement("orderNo", box.NId),
            new XElement("kanban", box.CustomerKanban),
            new XElement("part", box.MaterialNId),
            new XElement("box", n.ToString(CultureInfo.InvariantCulture)),
            new XElement("totalBoxes", box.TotalBoxes.ToString(CultureInfo.InvariantCulture)))).ToList();
        Shipments.Label(platform, "BoxLabelTemplate", labels);
        return new ShipmentResponse { NId = line.NId, Labels = labels.Count };
    }
}

/// <summary>
/// Quét nhãn skid ở màn staging. Họ A: nhãn là mã skid đã in. Họ B: tách mã nhãn (đọc phần trước ký tự mặt, nên mặt A hay B ra cùng
/// một skid), tạo skid nếu chưa có; mỗi chuyến chỉ dựng một skid một lúc. Đơn và nhóm sang Staging.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class ScanSkidLabel : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }
}

internal sealed class ScanSkidLabelHandler : ICommandHandler<ScanSkidLabel, ShipmentResponse>
{
    public ShipmentResponse Handle(ScanSkidLabel command, IPlatform platform)
    {
        var barcode = command.Barcode!.Trim();
        var skid = platform.Query<Skid>().SingleOrDefault(s => s.NId == barcode);
        if (skid is null && ShipBarcodes.ParseSkid(barcode) is { } parsed)
        {
            var sheet = SheetSkid(platform, barcode[..ShipBarcodes.SkidKeyLength], parsed);
            if (sheet.Error is not null)
            {
                return sheet.Error;
            }

            skid = sheet.Skid!;
        }

        if (skid is null)
        {
            return ShipmentResponse.Fail(NvErrors.UnknownSkid, $"{barcode} is not a skid label of an open delivery.");
        }

        var delivery = platform.Query<Delivery>().Single(d => d.NId == skid.DeliveryNId);
        if (delivery.Status is DeliveryStates.Departed)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidShipmentState, $"Delivery {delivery.NId} has departed.");
        }

        var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
        if (group.Status is OrderStates.New or OrderStates.Released)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidShipmentState, $"Pick list group {group.NId} is {group.Status}; complete the pick list first.");
        }

        if (skid.Status == SkidStates.Printed)
        {
            skid.Status = SkidStates.Building;
            platform.Submit(skid);
        }

        if (group.Status == OrderStates.Picked)
        {
            group.Status = OrderStates.Staging;
            platform.Submit(group);
            Shipments.SetOrders(platform, group.NId, OrderStates.Staging, o => o.Status == OrderStates.Picked);
        }

        var (todo, completed) = Shipments.Progress(platform, delivery);
        return new ShipmentResponse { NId = skid.NId, Status = skid.Status, GroupNId = group.NId, DeliveryNId = delivery.NId, Todo = todo, Completed = completed };
    }

    /// <summary>
    /// Skid họ B của nhãn: đơn mở khớp mã đơn khách, dock, nhà cung cấp, route của nhãn; skid đã có của chuyến thì trả nó, chưa có thì
    /// tạo. NId gồm cả chuyến: một đơn khách giao nhiều chuyến thì nhãn skid 001 của chuyến sau trùng mã với chuyến trước.
    /// </summary>
    private static (Skid? Skid, ShipmentResponse? Error) SheetSkid(IPlatform platform,
        string key, (string Route, string CustomerOrder, string Dock, string Supplier, string ShipTo, int SkidNo) label)
    {
        var line = platform.Query<SalesOrderLine>()
            .Where(l => l.CustomerOrderNo == label.CustomerOrder && l.Dock == label.Dock && l.SupplierCode == label.Supplier).ToList()
            .Select(l => (Line: l, Order: platform.Query<SalesOrder>().Single(o => o.NId == l.OrderNId)))
            .FirstOrDefault(x => x.Order.Route == label.Route && x.Order.Status != OrderStates.Shipped);
        if (line.Line is null)
        {
            return (null, ShipmentResponse.Fail(NvErrors.UnknownSkid, $"No open order {label.CustomerOrder} on route {label.Route} at dock {label.Dock}."));
        }

        if (label.SkidNo < 1 || label.SkidNo > (int)Math.Ceiling(line.Line.Quantity / line.Line.ContainerQuantity))
        {
            return (null, ShipmentResponse.Fail(NvErrors.UnknownSkid, $"Order {label.CustomerOrder} has no skid {label.SkidNo}."));
        }

        var delivery = platform.Query<PickListGroup>().Where(g => g.NId == line.Order.GroupNId).Select(g => g.DeliveryNId).Single();
        if (platform.Query<Skid>().SingleOrDefault(s => s.NId == delivery + "|" + key) is { } existing)
        {
            return (existing, null);
        }

        if (platform.Query<Skid>().FirstOrDefault(s => s.DeliveryNId == delivery && s.Status == SkidStates.Building) is { } open)
        {
            return (null, ShipmentResponse.Fail(NvErrors.SkidInProgress, $"Skid {open.SkidNo:000} of this delivery is still being built; finish it first."));
        }

        var skid = platform.Create<Skid>();
        skid.NId = delivery + "|" + key;
        skid.DeliveryNId = delivery;
        skid.OrderLineNId = line.Line.NId;
        skid.SkidNo = label.SkidNo;
        skid.Status = SkidStates.Building;
        platform.Submit(skid);
        return (skid, null);
    }
}

/// <summary>
/// Quét pallet vào skid đang dựng: pallet phải đã được lấy cho nhóm đơn này. Gắn pallet vào skid, pallet sang Staged và chuyển vào
/// khu tập kết của nhóm route chính. Họ A: tự ghi serial hàng trên pallet vào skid, skid xong ngay. Họ B: skid xong khi quét đủ
/// nhãn hộp.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class StagePallet : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? SkidNId { get; init; }

    [Mandatory]
    public string? PalletNId { get; init; }
}

internal sealed class StagePalletHandler : ICommandHandler<StagePallet, ShipmentResponse>
{
    public ShipmentResponse Handle(StagePallet command, IPlatform platform)
    {
        var skid = platform.Query<Skid>().SingleOrDefault(s => s.NId == command.SkidNId);
        if (skid is null || skid.Status != SkidStates.Building)
        {
            return ShipmentResponse.Fail(NvErrors.UnknownSkid, $"Skid {command.SkidNId} is not being built; scan its label first.");
        }

        if (skid.PalletNId is not null)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidShipmentState, $"Skid {skid.NId} already holds pallet {skid.PalletNId}.");
        }

        var delivery = platform.Query<Delivery>().Single(d => d.NId == skid.DeliveryNId);
        var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
        var picked = platform.Query<PickedPallet>().SingleOrDefault(p => p.NId == group.NId + "|" + command.PalletNId);
        if (picked is null || picked.Status != PalletStates.Picked)
        {
            return ShipmentResponse.Fail(NvErrors.NotPicked, $"Pallet {command.PalletNId} is not on the pick list of {group.Name}.");
        }

        var line = platform.Query<SalesOrderLine>().Single(l => l.NId == skid.OrderLineNId);
        var pallet = platform.Query<Mtua>().Single(c => c.NId == picked.PalletNId);
        if (pallet.MaterialNId != line.MaterialNId)
        {
            return ShipmentResponse.Fail(NvErrors.MaterialMismatch, $"Pallet {pallet.NId} holds {pallet.MaterialNId}; skid {skid.SkidNo:000} is for {line.MaterialNId}.");
        }

        if (Shipments.RouteLocations(platform, group.Route!, "Staging").FirstOrDefault() is not { } area)
        {
            return ShipmentResponse.Fail(NvErrors.NoRouteArea, $"Equipment group of route {Shipments.MainRoute(group.Route!)} has no staging area.");
        }

        var staged = platform.CallCommand<SetMtuaStatus, ProductResponse>(new SetMtuaStatus { NId = pallet.NId, Status = PalletStates.Staged });
        var moved = staged.Succeeded ? platform.CallCommand<MoveMtua, ProductResponse>(new MoveMtua { NId = pallet.NId, EquipmentNId = area }) : staged;
        if (!moved.Succeeded)
        {
            return ShipmentResponse.From(moved);
        }

        picked.Status = PalletStates.Staged;
        platform.Submit(picked);
        skid.PalletNId = pallet.NId;
        platform.Submit(skid);
        if (Shipments.FamilyOf(platform, group.NId) == ShipFamilies.A)
        {
            foreach (var item in PackingData.ItemsIn(platform, pallet.NId))
            {
                var content = platform.Create<SkidContent>();
                content.NId = skid.NId + "|" + item.NId;
                content.SkidNId = skid.NId;
                content.ItemNId = item.NId;
                platform.Submit(content);
            }

            return Staging.Complete(platform, skid, line, picked, delivery, group);
        }

        var (todo, completed) = Shipments.Progress(platform, delivery);
        return new ShipmentResponse { NId = skid.NId, Status = skid.Status, GroupNId = group.NId, DeliveryNId = delivery.NId, Todo = todo, Completed = completed };
    }
}

/// <summary>
/// Quét nhãn hộp vào skid họ B đang dựng: nhãn phải khớp nhà cung cấp, dock, part, mã đơn khách của dòng đơn; nhãn đã quét thì chặn.
/// Tổng hàng của các hộp đã quét bằng số hàng của pallet thì skid xong.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class ScanBoxLabel : ICommand<ShipmentResponse>
{
    [Mandatory]
    public string? SkidNId { get; init; }

    [Mandatory]
    public string? Barcode { get; init; }
}

internal sealed class ScanBoxLabelHandler : ICommandHandler<ScanBoxLabel, ShipmentResponse>
{
    public ShipmentResponse Handle(ScanBoxLabel command, IPlatform platform)
    {
        var skid = platform.Query<Skid>().SingleOrDefault(s => s.NId == command.SkidNId);
        if (skid is null || skid.Status != SkidStates.Building || skid.PalletNId is null)
        {
            return ShipmentResponse.Fail(NvErrors.UnknownSkid, $"Skid {command.SkidNId} has no pallet being built; scan the skid label and the pallet first.");
        }

        if (ShipBarcodes.ParseBox(command.Barcode!) is not { } box)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidBoxLabel, $"{command.Barcode} is not a box label ({ShipBarcodes.BoxLabelLength} characters).");
        }

        var line = platform.Query<SalesOrderLine>().Single(l => l.NId == skid.OrderLineNId);
        if (box.Supplier != line.SupplierCode || box.Dock != line.Dock || box.Material != line.MaterialNId || box.CustomerOrder != line.CustomerOrderNo)
        {
            return ShipmentResponse.Fail(NvErrors.InvalidBoxLabel,
                $"Box label is for order {box.CustomerOrder}, {box.Material}, supplier {box.Supplier}, dock {box.Dock}; the skid is for {line.CustomerOrderNo}, {line.MaterialNId}, {line.SupplierCode}, {line.Dock}.");
        }

        var nid = skid.NId + "|" + box.CustomerOrder + "-" + box.BoxNo.ToString("000", CultureInfo.InvariantCulture);
        var skids = platform.Query<Skid>().Where(s => s.OrderLineNId == line.NId).Select(s => s.NId).ToList();
        if (platform.Query<SkidContent>().Any(c => skids.Contains(c.SkidNId!) && c.CustomerOrderNo == box.CustomerOrder && c.BoxNo == box.BoxNo))
        {
            return ShipmentResponse.Fail(NvErrors.BoxScanned, $"Box {box.BoxNo} of {box.TotalBoxes} of order {box.CustomerOrder} is already scanned.");
        }

        var content = platform.Create<SkidContent>();
        content.NId = nid;
        content.SkidNId = skid.NId;
        content.CustomerOrderNo = box.CustomerOrder;
        content.BoxNo = box.BoxNo;
        platform.Submit(content);

        var delivery = platform.Query<Delivery>().Single(d => d.NId == skid.DeliveryNId);
        var group = platform.Query<PickListGroup>().Single(g => g.NId == delivery.GroupNId);
        var picked = platform.Query<PickedPallet>().Single(p => p.NId == group.NId + "|" + skid.PalletNId);
        var perBox = platform.Query<CustomerBoxOrder>().Where(b => b.NId == line.CustomerOrderNo).Select(b => b.QuantityPerBox).SingleOrDefault();
        var boxes = platform.Query<SkidContent>().Count(c => c.SkidNId == skid.NId);
        if (boxes * (perBox > 0 ? perBox : 1m) >= picked.Quantity)
        {
            return Staging.Complete(platform, skid, line, picked, delivery, group);
        }

        var (todo, completed) = Shipments.Progress(platform, delivery);
        return new ShipmentResponse { NId = skid.NId, Status = skid.Status, GroupNId = group.NId, DeliveryNId = delivery.NId, Todo = todo, Completed = completed };
    }
}

internal static class Staging
{
    /// <summary>Skid xong: dòng đơn cộng lượng đã staged; đơn đủ pallet đã lấy thì Staged; cả nhóm xong thì gửi email danh sách pallet.</summary>
    public static ShipmentResponse Complete(IPlatform platform, Skid skid, SalesOrderLine line, PickedPallet picked, Delivery delivery, PickListGroup group)
    {
        skid.Status = SkidStates.Staged;
        platform.Submit(skid);
        line.StagedQuantity += picked.Quantity;
        platform.Submit(line);

        var pending = platform.Query<PickedPallet>().Where(p => p.GroupNId == group.NId && p.Status == PalletStates.Picked)
            .Select(p => p.OrderLineNId).ToList();
        var pendingOrders = platform.Query<SalesOrderLine>().Where(l => pending.Contains(l.NId)).Select(l => l.OrderNId).Distinct().ToList();
        Shipments.SetOrders(platform, group.NId, OrderStates.Staged, o => o.Status == OrderStates.Staging && !pendingOrders.Contains(o.NId));
        string? warning = null;
        if (pending.Count == 0)
        {
            group.Status = OrderStates.Staged;
            platform.Submit(group);
            warning = MailStagedPallets(platform, group, delivery);
        }

        var (todo, completed) = Shipments.Progress(platform, delivery);
        return new ShipmentResponse
        {
            NId = skid.NId,
            Status = skid.Status,
            GroupNId = group.NId,
            DeliveryNId = delivery.NId,
            Todo = todo,
            Completed = completed,
            Warning = warning,
        };
    }

    /// <summary>Email danh sách pallet, tote, hàng của chuyến tới người nhận của nơi nhận. Không có người nhận: staging vẫn xong, trả cảnh báo.</summary>
    public static string? MailStagedPallets(IPlatform platform, PickListGroup group, Delivery delivery)
    {
        var recipients = Shipments.ShipTo(platform, group.ShipTo)?.StagedPalletRecipients;
        if (string.IsNullOrWhiteSpace(recipients))
        {
            platform.Tracer.Write(TraceLevel.Warning, $"No staged pallet recipients for {group.ShipTo}; mail of delivery {delivery.NId} not sent.");
            return $"Staged pallet mail not sent: no recipients set for {group.ShipTo}.";
        }

        var pallets = platform.Query<PickedPallet>().Where(p => p.GroupNId == group.NId).Select(p => p.PalletNId!).ToList();
        var body = new XElement("mail",
            new XElement("to", recipients),
            new XElement("subject", $"Staged pallets {group.Name}"),
            new XElement("pallets", pallets.Select(p => new XElement("pallet", new XAttribute("id", p),
                PackingData.ItemsIn(platform, p).Select(m => new XElement("item", new XAttribute("tote", m.ParentMtuaNId ?? string.Empty), m.NId))))));
        platform.SendMessageAsync(new OutboundMessage(ShipAdapters.Mail, "StagedPallets", body.ToString(), "application/xml"));
        return null;
    }
}

public sealed class GroupParameters
{
    public string? GroupNId { get; init; }
}

/// <summary>Pallet đề xuất cho nhóm đơn (tab đề xuất của màn Picking): theo FIFO, cho từng part còn thiếu của nhóm.</summary>
[ReadingFunction(ShipApp.Name)]
public sealed class GroupSuggestedPallets : IReadingFunction<GroupParameters, SuggestedPalletRow>
{
    public IQueryable<SuggestedPalletRow> Read(GroupParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var orders = platform.ProjectionQuery<SalesOrder>().Where(o => o.GroupNId == parameters.GroupNId).Select(o => o.NId).ToList();
        var materials = platform.ProjectionQuery<SalesOrderLine>().Where(l => orders.Contains(l.OrderNId!) && l.PickedQuantity < l.Quantity)
            .Select(l => l.MaterialNId!).Distinct().ToList();
        if (materials.Count == 0)
        {
            return Enumerable.Empty<SuggestedPalletRow>().AsQueryable();
        }

        var connection = FinishedGoodsSql.Connection(platform.ProjectionQuery<ConfigurationKey>);
        return materials.SelectMany(m => FinishedGoodsSql.Suggested(connection, m)).AsQueryable();
    }
}
