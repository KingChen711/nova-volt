using System.Text.Json.Nodes;
using System.Xml.Linq;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Product;
using Nvm.Project;
using Nvm.Solution;

namespace Nvm.SolutionTests;

/// <summary>
/// R8: xuất hàng hai họ tới Shipped, hệ của khách không trả lời khi chất xe họ B, ma trận ngoại lệ. Pallet dựng bằng command và cất
/// kho bằng lệnh của R7; đơn bán vào bằng lệnh nhận payload như ERP gửi (trước R11 chưa có gateway vào).
/// </summary>
public sealed class ShipmentTests
{
    private static readonly CallerIdentity Shipper = SolutionHarness.Caller("ship.nv1", "shipping");
    private static readonly CallerIdentity YardUser = SolutionHarness.Caller("yard.nv1", "yard");

    [Fact]
    public async Task Family_a_runs_from_sales_order_to_shipped_and_sends_the_asn()
    {
        var (h, gateway) = await Sh.StackAsync();
        Fg.TestedPallet(h, "SA-P1");
        h.Time.Advance(TimeSpan.FromHours(1));
        Fg.TestedPallet(h, "SA-P2");
        Sh.Store(h, "SA-P1", "NV1-CMN-FGW-A01");
        Sh.Store(h, "SA-P2", "NV1-CMN-FGW-A02");

        // Hai đơn cùng nơi nhận, cùng giờ giao: một nhóm; route = 3 ký tự nơi nhận + yyMMddHHmm.
        var acks = Sh.Orders(h, Sh.OrderA("5100000001", 8), Sh.OrderA("5100000002", 8));
        acks.ShouldAllBe(a => a.Accepted);
        acks.Select(a => a.GroupNId).Distinct().ShouldHaveSingleItem();
        var group = Sh.Group(h, acks[0].GroupNId!);
        (group.Route, group.Status).ShouldBe(("KLN2610271200", OrderStates.New));
        var delivery = group.DeliveryNId!;

        // Picking: chưa Release thì không lấy; lấy lệch đề xuất không bị chặn, chỉ hạ cờ FIFO; vị trí giữ ô kho.
        Fg.Error(h.Ship(new PickPallet { GroupNId = group.NId, PalletNId = "SA-P2" }, Shipper)).ShouldBe(NvErrors.InvalidShipmentState);
        Sh.Ok(h.Ship(new ReleasePickListGroup { GroupNId = group.NId }, Shipper));
        var off = h.Ship(new PickPallet { GroupNId = group.NId, PalletNId = "SA-P2" }, Shipper);
        Sh.Ok(off);
        off.As<ShipmentResponse>().SuggestedPalletNId.ShouldBe("SA-P1");
        h.Read<SalesOrder>(q => q.Where(o => o.NId == "5100000001")).Single().FifoPicked.ShouldBeFalse();
        Sh.Ok(h.Ship(new PickPallet { GroupNId = group.NId, PalletNId = "SA-P1" }, Shipper));
        var picked = Fg.Container(h, "SA-P1");
        (picked.Status, picked.EquipmentNId).ShouldBe((PalletStates.Picked, "NV1-CMN-FGW-A01"));
        Sh.Ok(h.Ship(new CompletePickList { GroupNId = group.NId }, Shipper));

        // Nhãn họ A: một nhãn mỗi skid, bản ghi skid có từ lúc in (16 kit / 8 mỗi kiện = 2 skid).
        h.Ship(new PrintSkidLabels { GroupNId = group.NId }, Shipper).As<ShipmentResponse>().Labels.ShouldBe(2);
        var tags = gateway.Labels("SkidTagTemplate");
        tags.Count.ShouldBe(2);
        h.Read<Skid>(q => q.Where(s => s.DeliveryNId == delivery)).Select(s => s.Status).ShouldAllBe(s => s == SkidStates.Printed);

        // Staging: pallet chưa lấy cho nhóm này bị từ chối; pallet đúng thì Staged ở khu tập kết của route KLN, skid xong ngay.
        Fg.TestedPallet(h, "SA-PX");
        var scanned = h.Ship(new ScanSkidLabel { Barcode = tags[0] }, Shipper);
        Sh.Ok(scanned);
        Fg.Error(h.Ship(new StagePallet { SkidNId = tags[0], PalletNId = "SA-PX" }, Shipper)).ShouldBe(NvErrors.NotPicked);
        var first = h.Ship(new StagePallet { SkidNId = tags[0], PalletNId = "SA-P1" }, Shipper).As<ShipmentResponse>();
        (first.Todo, first.Completed).ShouldBe((1, 1));
        var staged = Fg.Container(h, "SA-P1");
        (staged.Status, staged.EquipmentNId).ShouldBe((PalletStates.Staged, "NV1-CMN-STG-A"));
        h.Read<SkidContent>(q => q.Where(c => c.SkidNId == tags[0])).Count.ShouldBe(8);
        Sh.Ok(h.Ship(new ScanSkidLabel { Barcode = tags[1] }, Shipper));
        Sh.Ok(h.Ship(new StagePallet { SkidNId = tags[1], PalletNId = "SA-P2" }, Shipper));
        Sh.Group(h, group.NId).Status.ShouldBe(OrderStates.Staged);
        gateway.Posted.ShouldContain(m => m.Adapter == ShipAdapters.Mail && m.MessageType == "StagedPallets");

        // Xe của khách: route pool phải trùng nơi nhận; vào dock rồi mới chất được.
        Sh.Ok(h.Yard(new CreateTrailer { Barcode = "TRL-OTHER", RoutePool = "BRK1" }, YardUser));
        Fg.Error(h.Yard(new ArriveCustomerTrailer { Barcode = "TRL-OTHER", DeliveryNId = delivery, LocationNId = "NV1-CMN-YARD-Y02" }, YardUser))
            .ShouldBe(NvErrors.WrongRoutePool);
        Sh.Ok(h.Yard(new CreateTrailer { Barcode = "TRL-KLN", RoutePool = "KLN" }, YardUser));
        Sh.Ok(h.Yard(new ArriveCustomerTrailer { Barcode = "TRL-KLN", DeliveryNId = delivery, LocationNId = "NV1-CMN-YARD-Y01" }, YardUser));
        Fg.Error(h.Ship(new CheckTruckLoading { DeliveryNId = delivery }, Shipper)).ShouldBe(NvErrors.NoTrailerAtDock);
        Sh.Dock(h, "TRL-KLN", "NV1-CMN-DOCK-D01");

        // Họ A: nút chất xe không hỏi ngoại lệ; Confirm không gửi tin đồng bộ nào.
        h.Ship(new CheckTruckLoading { DeliveryNId = delivery }, Shipper).As<LoadingCheckResponse>().Ready.ShouldBeTrue();
        var loaded = h.Ship(new ConfirmTruckLoad { DeliveryNId = delivery, SealNo = "SEAL-A1", Skids = [.. tags] }, Shipper);
        Sh.Ok(loaded);
        loaded.As<ShipmentResponse>().Status.ShouldBe(DeliveryStates.Loaded);
        gateway.Sent.ShouldBeEmpty();
        Fg.Container(h, "SA-P2").Status.ShouldBe(PalletStates.Loaded);
        var bol = h.Read<Delivery>(q => q.Where(d => d.NId == delivery)).Single().BillOfLadingNo;
        bol.ShouldNotBeNullOrEmpty();
        gateway.Labels("BillOfLadingTemplate", "billOfLading").ShouldBe([bol]);
        h.Read<Trailer>(q => q.Where(t => t.NId == "TRL-KLN")).Single().Status.ShouldBe(TrailerStates.CustomerLoaded);

        // Ra bãi chỉ vào ô của nhóm route; yard pass; xe rời.
        Fg.Error(h.Yard(new MoveTrailerOut { Barcode = "TRL-KLN", LocationNId = "NV1-CMN-YARD-Y02" }, YardUser)).ShouldBe(NvErrors.NoRouteArea);
        var outMove = h.Yard(new MoveTrailerOut { Barcode = "TRL-KLN", LocationNId = "NV1-CMN-YARD-Y04" }, YardUser).As<YardResponse>().NId!;
        Fg.Error(h.Yard(new DepartCustomerTrailer { Barcode = "TRL-KLN", YardPassNId = "x" }, YardUser)).ShouldBe(NvErrors.MovementOpen);
        Sh.Drop(h, outMove);
        Fg.Error(h.Yard(new DepartCustomerTrailer { Barcode = "TRL-KLN", YardPassNId = "x" }, YardUser)).ShouldBe(NvErrors.NoYardPass);
        var pass = h.Yard(new IssueYardPass { Barcode = "TRL-KLN" }, YardUser).As<YardResponse>().NId!;
        pass.ShouldStartWith("YP-");
        Sh.Ok(h.Yard(new DepartCustomerTrailer { Barcode = "TRL-KLN", YardPassNId = pass }, YardUser));

        group = Sh.Group(h, group.NId);
        group.Status.ShouldBe(OrderStates.Shipped);
        group.Name!.ShouldContain(" Shipped ");
        h.Read<SalesOrder>(q => q.Where(o => o.GroupNId == group.NId)).ShouldAllBe(o => o.Status == OrderStates.Shipped);
        var empty = Fg.Container(h, "SA-P1");
        (empty.Status, empty.Quantity, empty.EquipmentNId, empty.MaterialNId).ShouldBe((PalletStates.Empty, 0m, EquipmentLevels.NotApplicable, null));
        var tote = Fg.Container(h, "SA-P1-T01");
        (tote.Status, tote.ParentMtuaNId).ShouldBe((ToteStates.Empty, null));
        var kit = h.Read<Mtu>(q => q.Where(m => m.NId == "NV-FG-KIT-A_SA-P1-K01")).Single();
        (kit.Status, kit.ParentMtuaNId, kit.EquipmentNId).ShouldBe((MtuStatus.Shipped, null, EquipmentLevels.NotApplicable));
        var trailer = h.Read<Trailer>(q => q.Where(t => t.NId == "TRL-KLN")).Single();
        (trailer.Status, trailer.LocationNId, trailer.YardPassNId).ShouldBe((TrailerStates.Free, null, null));

        // ASN sau commit, bởi subscriber; phản hồi của khách thành email.
        gateway.Posted.ShouldNotContain(m => m.Adapter == ShipAdapters.Asn);
        h.DeliverEvents();
        var asn = XElement.Parse(gateway.Posted.Single(m => m.Adapter == ShipAdapters.Asn).Content);
        (asn.Element("billOfLading")!.Value, asn.Element("carrier")!.Value).ShouldBe((bol, "NVCR"));
        asn.Element("items")!.Elements().Count().ShouldBe(16);
        asn.Element("items")!.Elements().First().Attribute("tote")!.Value.ShouldStartWith("SA-P");
        Sh.Ok(h.Ship(new RecordAsnResponse { DocumentNo = bol, Status = "Accepted", Message = "ok" }));
        gateway.Posted.ShouldContain(m => m.Adapter == ShipAdapters.Mail && m.MessageType == "AsnResponse");

        // Đơn mới cùng nơi nhận, route, giờ giao: nhóm cũ đã giao nên vào nhóm mới.
        Sh.Orders(h, Sh.OrderA("5100000003", 8)).Single().GroupNId.ShouldNotBe(group.NId);
    }

    [Fact]
    public async Task Family_b_needs_the_box_order_scans_every_box_and_ships_after_the_customer_system_confirms()
    {
        var (h, gateway) = await Sh.StackAsync();
        Sh.BoxOrder(h, "C000000001", boxes: 12);
        Sh.PalletB(h, "SB-P1");
        Sh.PalletB(h, "SB-P2");

        // Họ B: không có hãng vận tải; route của khách trống thì tra dữ liệu đặt hàng; không có thì Ack lỗi. Thiếu trường cũng Ack lỗi.
        var acks = Sh.Orders(h, Sh.OrderB("5200000001", "C000000001", 12), Sh.OrderB("5200000002", "C999999999", 6),
            new JsonObject { ["orderNo"] = "5200000003", ["shipTo"] = "BRK1" });
        acks.Select(a => a.Accepted).ShouldBe([true, false, false]);
        acks[1].Message!.ShouldContain("No route");
        acks[2].Message!.ShouldContain("DeliveryDate");
        var group = Sh.Group(h, acks[0].GroupNId!);
        group.Route.ShouldBe("ZR4K02");
        var delivery = group.DeliveryNId!;

        Sh.Pick(h, group.NId, "SB-P1", "SB-P2");
        h.Ship(new PrintSkidLabels { GroupNId = group.NId }, Shipper).As<ShipmentResponse>().Labels.ShouldBe(4);
        var sheets = gateway.Labels("SkidSheetTemplate");
        sheets.ShouldAllBe(s => s.Length == 35);
        h.Read<Skid>().ShouldBeEmpty();
        h.Ship(new PrintBoxLabels { OrderLineNId = "5200000001-10" }, Shipper).As<ShipmentResponse>().Labels.ShouldBe(12);
        var boxes = gateway.Labels("BoxLabelTemplate");

        // Mặt B của skid 001 ra cùng skid với mặt A; skid 002 chưa dựng được khi 001 còn dở; hộp đã quét thì chặn.
        var skid1 = h.Ship(new ScanSkidLabel { Barcode = sheets[1] }, Shipper).As<ShipmentResponse>().NId!;
        h.Ship(new ScanSkidLabel { Barcode = sheets[0] }, Shipper).As<ShipmentResponse>().NId.ShouldBe(skid1);
        Sh.Ok(h.Ship(new StagePallet { SkidNId = skid1, PalletNId = "SB-P1" }, Shipper));
        Fg.Error(h.Ship(new ScanSkidLabel { Barcode = sheets[2] }, Shipper)).ShouldBe(NvErrors.SkidInProgress);
        Sh.Ok(h.Ship(new ScanBoxLabel { SkidNId = skid1, Barcode = boxes[0] }, Shipper));
        Fg.Error(h.Ship(new ScanBoxLabel { SkidNId = skid1, Barcode = boxes[0] }, Shipper)).ShouldBe(NvErrors.BoxScanned);
        Fg.Error(h.Ship(new ScanBoxLabel { SkidNId = skid1, Barcode = "X" + boxes[1][1..] }, Shipper)).ShouldBe(NvErrors.InvalidBoxLabel);
        for (var i = 1; i < 6; i++)
        {
            Sh.Ok(h.Ship(new ScanBoxLabel { SkidNId = skid1, Barcode = boxes[i] }, Shipper));
        }

        h.Read<Skid>(q => q.Where(s => s.NId == skid1)).Single().Status.ShouldBe(SkidStates.Staged);
        var skid2 = h.Ship(new ScanSkidLabel { Barcode = sheets[2] }, Shipper).As<ShipmentResponse>().NId!;
        Sh.Ok(h.Ship(new StagePallet { SkidNId = skid2, PalletNId = "SB-P2" }, Shipper));
        foreach (var box in boxes.Skip(6))
        {
            Sh.Ok(h.Ship(new ScanBoxLabel { SkidNId = skid2, Barcode = box }, Shipper));
        }

        Sh.Group(h, group.NId).Status.ShouldBe(OrderStates.Staged);
        Fg.Container(h, "SB-P2").EquipmentNId.ShouldBe("NV1-CMN-STG-B");

        Sh.Trailer(h, "TRL-BRK", "BRK1", delivery, "NV1-CMN-YARD-Y01", "NV1-CMN-DOCK-D02");
        var check = h.Ship(new CheckTruckLoading { DeliveryNId = delivery }, Shipper).As<LoadingCheckResponse>();
        (check.Ready, check.MissingGroup).ShouldBe((true, null));
        Sh.Ok(h.Ship(new ConfirmTruckLoad { DeliveryNId = delivery, SealNo = "SEAL-B1", Skids = [sheets[0], sheets[3]] }, Shipper));

        // Hai tin đồng bộ theo thứ tự, số xác nhận và số chuyến ghi vào chuyến.
        gateway.Sent.Select(m => m.MessageType).ShouldBe(["SkidBuild", "ShipLoad"]);
        JsonNode.Parse(gateway.Sent[0].Content)!["orders"]![0]!["skids"]!.AsArray().Count.ShouldBe(2);
        var loaded = h.Read<Delivery>(q => q.Where(d => d.NId == delivery)).Single();
        (loaded.Status, loaded.ConfirmationNo, loaded.TripNo).ShouldBe((DeliveryStates.Loaded, "CF-1", "TP-1"));

        Sh.Depart(h, "TRL-BRK", "NV1-CMN-YARD-Y05");
        Sh.Group(h, group.NId).Status.ShouldBe(OrderStates.Shipped);
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-FG-MOD-B60_SB-P2-K06")).Single().Status.ShouldBe(MtuStatus.Shipped);
        h.DeliverEvents();
        gateway.Posted.ShouldNotContain(m => m.Adapter == ShipAdapters.Asn);
    }

    [Fact]
    public async Task Customer_system_not_replying_keeps_pallets_staged_and_the_trailer_unloaded()
    {
        var (h, gateway) = await Sh.StackAsync();
        var (delivery, sheet) = Sh.StagedB(h, gateway, "NR", "C000000010", "2026-10-27", "1300");

        gateway.CustomerReplies = false;
        var silent = h.Ship(new ConfirmTruckLoad { DeliveryNId = delivery, SealNo = "SEAL-N", Skids = [sheet] }, Shipper);
        Fg.Error(silent).ShouldBe(NvErrors.NotConfirmed);
        Fg.Message(silent).ShouldContain("SkidBuild");
        Fg.Container(h, "NR-P1").Status.ShouldBe(PalletStates.Staged);
        var open = h.Read<Delivery>(q => q.Where(d => d.NId == delivery)).Single();
        (open.Status, open.ConfirmationNo).ShouldBe((DeliveryStates.Arrived, null));
        h.Read<Trailer>(q => q.Where(t => t.NId == "TRL-NR")).Single().Status.ShouldBe(TrailerStates.CustomerEmpty);
        h.Read<Skid>(q => q.Where(s => s.DeliveryNId == delivery)).ShouldAllBe(s => s.Status == SkidStates.Staged);

        // Trả lời mà thiếu số xác nhận: cũng không chất.
        gateway.CustomerReplies = true;
        gateway.ShipLoadReply = "{}";
        Fg.Error(h.Ship(new ConfirmTruckLoad { DeliveryNId = delivery, SealNo = "SEAL-N", Skids = [sheet] }, Shipper)).ShouldBe(NvErrors.NotConfirmed);
        Fg.Container(h, "NR-P1").Status.ShouldBe(PalletStates.Staged);

        // Môi trường không có gateway (mặc định của nền): cùng kết quả.
        h.Executor.Gateway = new MissingMessageGateway();
        Fg.Message(h.Ship(new ConfirmTruckLoad { DeliveryNId = delivery, SealNo = "SEAL-N", Skids = [sheet] }, Shipper)).ShouldContain("No client gateway");
        Fg.Container(h, "NR-P1").Status.ShouldBe(PalletStates.Staged);
    }

    [Fact]
    public async Task Loading_exceptions_are_asked_by_group_in_order_and_the_automatic_code_is_declared_without_asking()
    {
        var (h, gateway) = await Sh.StackAsync();
        LoadingCheckResponse Check(string delivery) => h.Ship(new CheckTruckLoading { DeliveryNId = delivery }, Shipper).As<LoadingCheckResponse>();

        // 1. Họ A giao thiếu: không hỏi ngoại lệ.
        Fg.TestedPallet(h, "XA-P1");
        Sh.Store(h, "XA-P1", "NV1-CMN-FGW-A01");
        var groupA = Sh.Group(h, Sh.Orders(h, Sh.OrderA("5300000001", 8, quantity: 16)).Single().GroupNId!);
        Sh.Pick(h, groupA.NId, ["XA-P1"], acceptShort: true);
        h.Ship(new PrintSkidLabels { GroupNId = groupA.NId }, Shipper);
        var tag = gateway.Labels("SkidTagTemplate")[0];
        Sh.Ok(h.Ship(new ScanSkidLabel { Barcode = tag }, Shipper));
        Sh.Ok(h.Ship(new StagePallet { SkidNId = tag, PalletNId = "XA-P1" }, Shipper));
        Sh.Trailer(h, "TRL-XA", "KLN", groupA.DeliveryNId!, "NV1-CMN-YARD-Y01", "NV1-CMN-DOCK-D01");
        (Check(groupA.DeliveryNId!).Ready, Check(groupA.DeliveryNId!).MissingGroup).ShouldBe((true, null));

        // 2. Họ B đủ hàng: không hỏi (luồng trọn ở test họ B). 3. Họ B giao thiếu một đơn tại một dock: SkidBuild cho đúng đơn đó,
        // chỉ mã SkidBuild; khai xong thì qua.
        var (short1, _) = Sh.StagedB(h, gateway, "XS", "C000000020", "2026-10-28", "0800", ordered: 12, trailerAt: ("NV1-CMN-YARD-Y02", "NV1-CMN-DOCK-D02"));
        var asked = Check(short1);
        (asked.Ready, asked.MissingGroup, asked.OrderNId, asked.Dock).ShouldBe((false, ExceptionGroups.SkidBuild, "XS-SO", "Q2"));
        asked.Codes.ShouldBe(["SB1", "SB2", "SB3", "SB4"]);
        Fg.Error(h.Ship(new ConfirmTruckLoad { DeliveryNId = short1, SealNo = "S", Skids = [.. Sh.Skids(h, short1)] }, Shipper)).ShouldBe(NvErrors.ExceptionRequired);
        Fg.Error(h.Ship(new AddLoadingExceptionEntry { DeliveryNId = short1, OrderNId = "XS-SO", Code = "TR9" }, Shipper)).ShouldBe(NvErrors.UnknownExceptionCode);
        Sh.Ok(h.Ship(new AddLoadingExceptionEntry { DeliveryNId = short1, OrderNId = "XS-SO", Code = "SB3", Dock = "Q2", Comment = "short" }, Shipper));
        Fg.Error(h.Ship(new AddLoadingExceptionEntry { DeliveryNId = short1, OrderNId = "XS-SO", Code = "SB3", Dock = "Q2" }, Shipper)).ShouldBe(NvErrors.DuplicateException);
        Check(short1).Ready.ShouldBeTrue();

        // Tin SkidBuild mang ngoại lệ SkidBuild của đơn; ShipLoad không mang nó. Rồi chuyến này đi, giao một phần đơn khách C...20.
        Sh.Ok(h.Ship(new ConfirmTruckLoad { DeliveryNId = short1, SealNo = "S", Skids = [.. Sh.Skids(h, short1)] }, Shipper));
        var build = JsonNode.Parse(gateway.Sent[^2].Content)!;
        build["orders"]![0]!["exceptions"]![0]!["code"]!.GetValue<string>().ShouldBe("SB3");
        JsonNode.Parse(gateway.Sent[^1].Content)!["exceptions"]!.AsArray().ShouldBeEmpty();
        Sh.Depart(h, "TRL-XS", "NV1-CMN-YARD-Y06");

        // 4. Hai đơn trong chuyến, một đơn không có hàng staged và đơn kia thiếu: SkidBuild hỏi trước, rồi tới Trailer.
        Sh.BoxOrder(h, "C000000030", boxes: 12);
        Sh.BoxOrder(h, "C000000031", boxes: 6);
        Sh.PalletB(h, "XT-P1");
        var twoOrders = Sh.Orders(h, Sh.OrderB("XT-SO1", "C000000030", 12, date: "2026-10-29", time: "0900"),
            Sh.OrderB("XT-SO2", "C000000031", 6, date: "2026-10-29", time: "0900"));
        var groupT = Sh.Group(h, twoOrders[0].GroupNId!);
        twoOrders[1].GroupNId.ShouldBe(groupT.NId);
        Sh.StageB(h, gateway, groupT.NId, "XT-SO1-10", "XT-P1", acceptShort: true);
        Check(groupT.DeliveryNId!).MissingGroup.ShouldBe(ExceptionGroups.SkidBuild);
        Sh.Ok(h.Ship(new AddLoadingExceptionEntry { DeliveryNId = groupT.DeliveryNId, OrderNId = "XT-SO1", Code = "SB1", Dock = "Q2" }, Shipper));
        var trailerAsk = Check(groupT.DeliveryNId!);
        (trailerAsk.MissingGroup, trailerAsk.OrderNId).ShouldBe((ExceptionGroups.Trailer, "XT-SO2"));
        trailerAsk.Codes.ShouldBe(["TR1", "TR2", "TR3"]);

        // 5. Nhóm cùng nơi nhận, route, giờ giao đã giao trước đó: SkidLevel hỏi trước cả SkidBuild.
        var (again, _) = Sh.StagedB(h, gateway, "XL", "C000000040", "2026-10-28", "0800", ordered: 12, trailerAt: ("NV1-CMN-YARD-Y03", "NV1-CMN-DOCK-D03"));
        Check(again).MissingGroup.ShouldBe(ExceptionGroups.SkidLevel);
        Sh.Ok(h.Ship(new AddLoadingExceptionEntry { DeliveryNId = again, OrderNId = "XL-SO", Code = "SL1" }, Shipper));
        Check(again).MissingGroup.ShouldBe(ExceptionGroups.SkidBuild);

        // 6. Đơn khách C...20 đã giao một phần trên route này, nay staged phần còn lại ở giờ khác: MOM tự khai TR9, không hỏi; route
        // gửi đi mang tiền tố EX-.
        var (rest, _) = Sh.StagedB(h, gateway, "XR", "C000000020", "2026-10-30", "0800", ordered: 6, trailerAt: ("NV1-CMN-YARD-Y04", "NV1-CMN-DOCK-D04"),
            newBoxOrder: false);
        var auto = Check(rest);
        auto.Ready.ShouldBeTrue();
        auto.Declared.ShouldBe(["TR9"]);
        h.Read<LoadingExceptionEntry>(q => q.Where(e => e.DeliveryNId == rest)).Single().CodeNId.ShouldBe("TR9");
        Sh.Ok(h.Ship(new ConfirmTruckLoad { DeliveryNId = rest, SealNo = "S", Skids = [.. Sh.Skids(h, rest)] }, Shipper));
        var shipLoad = JsonNode.Parse(gateway.Sent[^1].Content)!;
        (shipLoad["route"]!.GetValue<string>(), shipLoad["exceptions"]![0]!["code"]!.GetValue<string>()).ShouldBe(("EX-ZR4K02", "TR9"));
    }

    [Fact]
    public async Task Picking_blocks_held_items_untested_family_a_pallets_and_too_young_family_b_items_by_key()
    {
        var (h, _) = await Sh.StackAsync();
        Fg.Pallet(h, "PK-A1", "NV-FG-KIT-A", totes: 8);
        h.Run(new SetConfigurationKey { NId = NvKeys.EolvValidation, Val = "false" }, app: SolutionManifest.FoundationAppName);
        Sh.Store(h, "PK-A1", "NV1-CMN-FGW-A01");
        h.Run(new SetConfigurationKey { NId = NvKeys.EolvValidation, Val = "true" }, app: SolutionManifest.FoundationAppName);
        var groupA = Sh.Group(h, Sh.Orders(h, Sh.OrderA("5400000001", 8)).Single().GroupNId!);
        Sh.Ok(h.Ship(new ReleasePickListGroup { GroupNId = groupA.NId }, Shipper));
        Fg.Error(h.Ship(new PickPallet { GroupNId = groupA.NId, PalletNId = "PK-A1" }, Shipper)).ShouldBe(NvErrors.NotTested);

        Sh.BoxOrder(h, "C000000050", boxes: 6);
        Sh.PalletB(h, "PK-B1");
        Sh.PalletB(h, "PK-B2", store: false);
        var groupB = Sh.Group(h, Sh.Orders(h, Sh.OrderB("5400000002", "C000000050", 6)).Single().GroupNId!);
        Sh.Ok(h.Ship(new ReleasePickListGroup { GroupNId = groupB.NId }, Shipper));
        Fg.Error(h.Ship(new PickPallet { GroupNId = groupB.NId, PalletNId = "PK-B2" }, Shipper)).ShouldBe(NvErrors.NotStored);
        h.Run(new SetConfigurationKey { NId = NvKeys.MinimumItemAgeHours, Val = "24" }, app: SolutionManifest.FoundationAppName);
        Fg.Error(h.Ship(new PickPallet { GroupNId = groupB.NId, PalletNId = "PK-B1" }, Shipper)).ShouldBe(NvErrors.ItemTooYoung);
        h.Time.Advance(TimeSpan.FromHours(25));
        Sh.Ok(h.Run(new HoldMtus { MtuNIds = ["NV-FG-MOD-B60_PK-B1-K03"] }));
        Fg.Error(h.Ship(new PickPallet { GroupNId = groupB.NId, PalletNId = "PK-B1" }, Shipper)).ShouldBe(NvErrors.HeldOrScrapped);
        Sh.Ok(h.Run(new ReleaseMtus { MtuNIds = ["NV-FG-MOD-B60_PK-B1-K03"] }));
        Sh.Ok(h.Ship(new PickPallet { GroupNId = groupB.NId, PalletNId = "PK-B1" }, Shipper));

        // Bỏ pick: pallet về Full, đề xuất lại có nó; đóng pick list khi chưa lấy gì thì từ chối.
        Sh.Ok(h.Ship(new UnpickPallet { GroupNId = groupB.NId, PalletNId = "PK-B1" }, Shipper));
        Fg.Container(h, "PK-B1").Status.ShouldBe(PalletStates.Full);
        h.Read<FifoPallet>(q => q.Where(f => f.NId == "PK-B1")).Single().Status.ShouldBe(PalletStates.Full);
        Fg.Error(h.Ship(new CompletePickList { GroupNId = groupB.NId }, Shipper)).ShouldBe(NvErrors.NotPicked);
        h.Ship(new PickPallet { GroupNId = groupB.NId, PalletNId = "PK-B1" }, SolutionHarness.Caller("op.nv1", "line-operator")).StatusCode.ShouldBe(403);
    }
}

/// <summary>Gateway giả trong test: ghi mọi tin; hệ của khách trả số xác nhận và số chuyến nếu được bật.</summary>
internal sealed class FakeGateway : IMessageGateway
{
    public List<OutboundMessage> Sent { get; } = [];

    public List<OutboundMessage> Posted { get; } = [];

    public bool CustomerReplies { get; set; } = true;

    public string ShipLoadReply { get; set; } = """{"confirmationNo":"CF-1","tripNo":"TP-1"}""";

    public MessageReply Send(OutboundMessage message, TimeSpan timeout)
    {
        if (!CustomerReplies)
        {
            return MessageReply.Failed($"No reply from {message.Adapter} within {timeout.TotalSeconds:0} s.");
        }

        Sent.Add(message);
        return MessageReply.Of(message.MessageType == "ShipLoad" ? ShipLoadReply : "{}");
    }

    public void Post(OutboundMessage message) => Posted.Add(message);

    /// <summary>Giá trị phần tử <paramref name="field"/> của các nhãn trong file in gần nhất của mẫu <paramref name="template"/>.</summary>
    public List<string> Labels(string template, string field = "barCode") =>
        [.. XElement.Parse(Posted.Last(m => m.MessageType == template).Content).Elements("label").Select(l => l.Element(field)!.Value)];
}

internal static class Sh
{
    private static readonly CallerIdentity Shipper = SolutionHarness.Caller("ship.nv1", "shipping");
    private static readonly CallerIdentity YardUser = SolutionHarness.Caller("yard.nv1", "yard");

    public static async Task<(SolutionHarness, FakeGateway)> StackAsync()
    {
        var h = await Fg.StackAsync();
        var gateway = new FakeGateway();
        h.Executor.Gateway = gateway;
        return (h, gateway);
    }

    public static void Ok(CommandResult result) => result.StatusCode.ShouldBe(200, result.Body.ToJsonString());

    public static PickListGroup Group(SolutionHarness h, string nid) => h.Read<PickListGroup>(q => q.Where(g => g.NId == nid)).Single();

    public static void Store(SolutionHarness h, string pallet, string cell)
    {
        Ok(h.Ship(new StorePallet { PalletNId = pallet, LocationNId = cell }, Shipper));
        h.DeliverEvents();
    }

    public static void PalletB(SolutionHarness h, string pallet, bool store = true)
    {
        Fg.Pallet(h, pallet, "NV-FG-MOD-B60", totes: 6);
        if (store)
        {
            Store(h, pallet, "NV1-CMN-FGW-B01");
        }
    }

    public static void BoxOrder(SolutionHarness h, string customerOrder, int boxes) => Ok(h.Ship(new SetCustomerBoxOrder
    {
        CustomerOrderNo = customerOrder,
        ShipTo = "BRK1",
        Dock = "Q2",
        SupplierCode = "NV100",
        CustomerKanban = "K7Q1",
        MaterialNId = "NV-FG-MOD-B60",
        QuantityPerBox = 1,
        TotalBoxes = boxes,
        RouteCode = "ZR4K02",
    }));

    public static JsonObject OrderA(string orderNo, int perSkid, int quantity = 8) => new()
    {
        ["orderNo"] = orderNo,
        ["customerReference"] = "REF-" + orderNo,
        ["shipTo"] = "KLN",
        ["deliveryDate"] = "2026-10-27",
        ["deliveryTime"] = "1200",
        ["carrierCode"] = "NVCR",
        ["items"] = new JsonArray(new JsonObject
        {
            ["item"] = "10",
            ["material"] = "NV-FG-KIT-A",
            ["quantity"] = quantity,
            ["containerQuantity"] = perSkid,
            ["supplierCode"] = "NV100",
        }),
    };

    public static JsonObject OrderB(string orderNo, string customerOrder, int quantity, string date = "2026-10-27", string time = "1400") => new()
    {
        ["orderNo"] = orderNo,
        ["customerReference"] = customerOrder,
        ["shipTo"] = "BRK1",
        ["deliveryDate"] = date,
        ["deliveryTime"] = time,
        ["items"] = new JsonArray(new JsonObject
        {
            ["item"] = "10",
            ["material"] = "NV-FG-MOD-B60",
            ["quantity"] = quantity,
            ["containerQuantity"] = 6,
            ["customerOrderNo"] = customerOrder,
            ["dock"] = "Q2",
            ["supplierCode"] = "NV100",
        }),
    };

    public static List<SalesOrderAck> Orders(SolutionHarness h, params JsonObject[] orders)
    {
        var result = h.Ship(new ReceiveSalesOrders { Payload = new JsonObject { ["orders"] = new JsonArray([.. orders]) }.ToJsonString() });
        Ok(result);
        return result.As<ShipmentResponse>().Acks;
    }

    public static void Pick(SolutionHarness h, string group, params string[] pallets) => Pick(h, group, pallets, acceptShort: false);

    public static void Pick(SolutionHarness h, string group, string[] pallets, bool acceptShort)
    {
        Ok(h.Ship(new ReleasePickListGroup { GroupNId = group }, Shipper));
        foreach (var pallet in pallets)
        {
            Ok(h.Ship(new PickPallet { GroupNId = group, PalletNId = pallet }, Shipper));
        }

        Ok(h.Ship(new CompletePickList { GroupNId = group, AcceptShort = acceptShort }, Shipper));
    }

    /// <summary>Kéo xe vào dock: tạo yêu cầu Move In rồi đi hết Accepted → Picked → Dropped.</summary>
    public static void Dock(SolutionHarness h, string trailer, string dock) =>
        Drop(h, h.Yard(new MoveTrailerIn { Barcode = trailer, DockNId = dock }, YardUser).As<YardResponse>().NId!);

    public static void Drop(SolutionHarness h, string movement)
    {
        foreach (var status in new[] { MovementStates.Accepted, MovementStates.Picked, MovementStates.Dropped })
        {
            Ok(h.Yard(new UpdateTrailerMovement { MovementNId = movement, Status = status }, YardUser));
        }
    }

    public static void Trailer(SolutionHarness h, string trailer, string routePool, string delivery, string yard, string dock)
    {
        Ok(h.Yard(new CreateTrailer { Barcode = trailer, RoutePool = routePool }, YardUser));
        Ok(h.Yard(new ArriveCustomerTrailer { Barcode = trailer, DeliveryNId = delivery, LocationNId = yard }, YardUser));
        Dock(h, trailer, dock);
    }

    public static void Depart(SolutionHarness h, string trailer, string yard)
    {
        Drop(h, h.Yard(new MoveTrailerOut { Barcode = trailer, LocationNId = yard }, YardUser).As<YardResponse>().NId!);
        var pass = h.Yard(new IssueYardPass { Barcode = trailer }, YardUser).As<YardResponse>().NId!;
        Ok(h.Yard(new DepartCustomerTrailer { Barcode = trailer, YardPassNId = pass }, YardUser));
    }

    public static List<string> Skids(SolutionHarness h, string delivery) =>
        [.. h.Read<Skid>(q => q.Where(s => s.DeliveryNId == delivery && s.Status == SkidStates.Staged)).Select(s => s.NId)];

    /// <summary>Pick một pallet B cho nhóm (đóng pick list), in nhãn, dựng skid đầu của dòng với đủ hộp.</summary>
    public static void StageB(SolutionHarness h, FakeGateway gateway, string group, string line, string pallet, bool acceptShort)
    {
        Pick(h, group, [pallet], acceptShort);
        Ok(h.Ship(new PrintSkidLabels { GroupNId = group }, Shipper));
        var sheet = gateway.Labels("SkidSheetTemplate").First(l => l.Contains(h.Read<SalesOrderLine>(q => q.Where(x => x.NId == line)).Single().CustomerOrderNo!, StringComparison.Ordinal));
        Ok(h.Ship(new PrintBoxLabels { OrderLineNId = line }, Shipper));
        var boxes = gateway.Labels("BoxLabelTemplate");
        var scanned = h.Ship(new ScanSkidLabel { Barcode = sheet }, Shipper);
        Ok(scanned);
        var skid = scanned.As<ShipmentResponse>().NId!;
        Ok(h.Ship(new StagePallet { SkidNId = skid, PalletNId = pallet }, Shipper));
        foreach (var box in boxes.Take(6))
        {
            Ok(h.Ship(new ScanBoxLabel { SkidNId = skid, Barcode = box }, Shipper));
        }
    }

    /// <summary>
    /// Một đơn họ B <c>&lt;prefix&gt;-SO</c> với một pallet <c>&lt;prefix&gt;-P1</c> (6 module) đã staged, xe <c>TRL-&lt;prefix&gt;</c> ở dock.
    /// Trả chuyến và nhãn skid 001 mặt A.
    /// </summary>
    public static (string Delivery, string Sheet) StagedB(SolutionHarness h, FakeGateway gateway, string prefix, string customerOrder, string date,
        string time, int ordered = 6, (string Yard, string Dock)? trailerAt = null, bool newBoxOrder = true)
    {
        if (newBoxOrder)
        {
            BoxOrder(h, customerOrder, boxes: ordered);
        }

        PalletB(h, prefix + "-P1");
        var group = Group(h, Orders(h, OrderB(prefix + "-SO", customerOrder, ordered, date, time)).Single().GroupNId!);
        StageB(h, gateway, group.NId, prefix + "-SO-10", prefix + "-P1", acceptShort: ordered > 6);
        var (yard, dock) = trailerAt ?? ("NV1-CMN-YARD-Y01", "NV1-CMN-DOCK-D01");
        Trailer(h, "TRL-" + prefix, "BRK1", group.DeliveryNId!, yard, dock);
        return (group.DeliveryNId!, gateway.Labels("SkidSheetTemplate")[0]);
    }
}
