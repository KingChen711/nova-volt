using System.Text.Json;
using System.Text.Json.Nodes;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Product;
using Nvm.Project;

namespace Nvm.SolutionTests;

/// <summary>
/// R10: một vòng kéo hàng tier 4 → 3 → 2 ra đúng chuỗi yêu cầu, ma trận trạng thái bảng gọi hàng, nhận nguyên liệu đặt thẳng, nạp lô vào
/// buffer của khu (lab L20), kanban và part address.
/// </summary>
public sealed class PullTests
{
    private const string Area = "NV1-B1-L1-MOD";
    private const string AreaSub = "NV1-B1-L1-MOD-ASSY";
    private const string CaseA = "NV-RM-CASE-A";
    private const string CaseB = "NV-RM-CASE-B";
    private const string Sep = "NV-RM-SEP";

    private static readonly CallerIdentity Line = SolutionHarness.Caller("op.nv1", "line-operator");
    private static readonly CallerIdentity Mover = SolutionHarness.Caller("log.nv1", "logistics");

    private static async Task<(SolutionHarness, FakeGateway)> StackAsync()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        var gateway = new FakeGateway();
        h.Executor.Gateway = gateway;
        return (h, gateway);
    }

    private static CommandResult Run(SolutionHarness h, object command, CallerIdentity? caller = null) => h.Run(command, caller, PullApp.Name);

    private static void Ok(CommandResult result) => result.StatusCode.ShouldBe(200, Fg.Message(result));

    private static string Lot(SolutionHarness h, string nid, string material, string buffer, decimal quantity = 300)
    {
        var equipment = h.Read<Nvm.Product.Buffer>(q => q.Where(b => b.NId == buffer)).Single().EquipmentNId;
        Ok(h.Run(new CreateMtu { NId = nid, MaterialNId = material, Quantity = quantity, EquipmentNId = equipment }));
        Ok(h.Run(new AttachMtuToBuffer { BufferNId = buffer, MtuNId = nid }));
        return nid;
    }

    private static string Call(SolutionHarness h, string route, string material, bool hot = false, string? address = null)
    {
        var result = Run(h, new CallMaterial { RouteNId = route, MaterialNId = material, Quantity = 300, Hot = hot, PartAddressNId = address }, Line);
        Ok(result);
        return result.Body["NId"]!.GetValue<string>();
    }

    private static TransportOperation TaskOf(SolutionHarness h, string request) =>
        h.Read<TransportOperation>(q => q.Where(t => t.LogisticRequestNId == request)).Single();

    private static void Finish(SolutionHarness h, string task, string lot)
    {
        Ok(Run(h, new RecordLot { TaskNId = task, MtuNId = lot }, Mover));
        Ok(Run(h, new CompletePullTask { TaskNId = task }, Mover));
    }

    private static string BufferOfLot(SolutionHarness h, string lot) => h.Read<BufferAssignment>(q => q.Where(a => a.NId == lot)).Single().BufferNId!;

    private static string EquipmentOf(SolutionHarness h, string lot) => h.Read<Mtu>(q => q.Where(m => m.NId == lot)).Single().EquipmentNId!;

    [Fact]
    public async Task One_pull_cycle_runs_tier_4_3_2_and_stops_with_the_right_chain_of_requests()
    {
        var (h, gateway) = await StackAsync();
        var first = Call(h, "RT-CASE-A", CaseA, address: CaseA + "|NV1-CMN-RCV-R01");

        // Một lần gọi: yêu cầu level 4 nguồn tier 3, việc chở Released, priority thường.
        var request = h.Read<LogisticRequest>(q => q.Where(r => r.NId == first)).Single();
        (request.SourceBufferNId, request.DestinationBufferNId, request.Priority).ShouldBe(("PC-T3-01", "PC-T4-01", (int?)2));
        var t4 = TaskOf(h, first);
        t4.Status.ShouldBe(TransportStatus.Released);
        h.Read<PullFlowProcess>().Single().Level.ShouldBe(4);

        var lot3 = Lot(h, "LOT-T3", CaseA, "PC-T3-01");
        Fg.Error(Run(h, new CompletePullTask { TaskNId = t4.NId }, Mover)).ShouldBe(NvErrors.NoLotRecorded);
        Ok(Run(h, new AcceptPullTask { TaskNId = t4.NId }, Mover));
        Ok(Run(h, new RecordLot { TaskNId = t4.NId, MtuNId = lot3 }, Mover));

        // Record chưa dời lô, chưa sinh chặng dưới.
        BufferOfLot(h, lot3).ShouldBe("PC-T3-01");
        h.Read<LogisticRequest>().Count.ShouldBe(1);
        Ok(Run(h, new CompletePullTask { TaskNId = t4.NId }, Mover));
        BufferOfLot(h, lot3).ShouldBe("PC-T4-01");
        EquipmentOf(h, lot3).ShouldBe("NV1-CMN-PC-T4-01");

        // Chặng 2 → 3: cùng material, cùng số lượng, không priority, chép part address; chưa có picklist.
        var level3 = h.Read<PullFlowProcess>(q => q.Where(p => p.Level == 3)).Single();
        level3.PartAddressNId.ShouldBe(CaseA + "|NV1-CMN-RCV-R01");
        level3.NId.ShouldStartWith("PR-T3-");
        var r3 = h.Read<LogisticRequest>(q => q.Where(r => r.NId == level3.NId)).Single();
        (r3.SourceBufferNId, r3.DestinationBufferNId, r3.Quantity, r3.Priority).ShouldBe(("PC-T2-01", "PC-T3-01", 300m, (int?)null));
        gateway.Posted.ShouldBeEmpty();

        Finish(h, TaskOf(h, level3.NId).NId, Lot(h, "LOT-T2", CaseA, "PC-T2-01"));

        // Chặng 1 → 2 lấy từ kho: picklist gửi WMS theo mã máy của ô đích.
        var level2 = h.Read<PullFlowProcess>(q => q.Where(p => p.Level == 2)).Single();
        var r2 = h.Read<LogisticRequest>(q => q.Where(r => r.NId == level2.NId)).Single();
        (r2.SourceBufferNId, r2.DestinationBufferNId).ShouldBe(("RCV-R01", "PC-T2-01"));
        var picklist = gateway.Posted.ShouldHaveSingleItem();
        (picklist.Adapter, picklist.MessageType).ShouldBe((PullAdapters.Wms, "Picklist"));
        var body = JsonNode.Parse(picklist.Content)!;
        body["machine"]!.GetValue<string>().ShouldBe("701");
        body["workOrder"]!.GetValue<string>().ShouldBe(level2.NId);

        // Level 2 xong là hết chuỗi.
        Finish(h, TaskOf(h, level2.NId).NId, Lot(h, "LOT-T1", CaseA, "RCV-R01"));
        h.Read<LogisticRequest>().Count.ShouldBe(3);
        h.Read<TransportOperation>().ShouldAllBe(t => t.Status == TransportStatus.Complete);
        gateway.Posted.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Route_without_tier_3_takes_from_tier_2_and_next_goes_straight_to_level_2()
    {
        var (h, gateway) = await StackAsync();
        var first = Call(h, "RT-CASE-B", CaseB);
        h.Read<LogisticRequest>(q => q.Where(r => r.NId == first)).Single().SourceBufferNId.ShouldBe("PC-T2-02");
        Finish(h, TaskOf(h, first).NId, Lot(h, "LOT-B", CaseB, "PC-T2-02"));

        h.Read<PullFlowProcess>(q => q.Where(p => p.NId != first)).Single().Level.ShouldBe(2);
        gateway.Posted.ShouldHaveSingleItem().MessageType.ShouldBe("Picklist");
    }

    [Fact]
    public async Task Picklist_is_skipped_when_the_cell_has_no_machine_code_and_stock_is_not_checked()
    {
        var (h, gateway) = await StackAsync();

        // Ô T2-03 không có mã máy: vẫn có yêu cầu bù, không có tin. Kệ nguồn còn lô khác cũng vẫn sinh yêu cầu bù.
        var first = Call(h, "RT-SEP", Sep);
        var lot = Lot(h, "LOT-S1", Sep, "PC-T2-03");
        Lot(h, "LOT-S2", Sep, "PC-T2-03");
        Finish(h, TaskOf(h, first).NId, lot);

        h.Read<PullFlowProcess>(q => q.Where(p => p.Level == 2)).ShouldHaveSingleItem();
        gateway.Posted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Call_refuses_a_material_missing_from_any_tier_of_the_route()
    {
        var (h, _) = await StackAsync();
        Ok(h.Run(new AddBufferWhitelist { BufferNId = "PC-T4-01", MaterialNId = CaseB }));

        var missing = Run(h, new CallMaterial { RouteNId = "RT-CASE-A", MaterialNId = CaseB, Quantity = 1 }, Line);
        Fg.Error(missing).ShouldBe(NvErrors.MaterialNotOnTier);
        Fg.Message(missing).ShouldContain("tier 3");
        Fg.Error(Run(h, new CallMaterial { RouteNId = "NONE", MaterialNId = CaseA, Quantity = 1 }, Line)).ShouldBe(NvErrors.UnknownRoute);
        h.Read<LogisticRequest>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Route_with_only_tier_4_makes_a_request_without_a_task()
    {
        var (h, _) = await StackAsync();
        Ok(h.Run(new SetDeliveryRoute { NId = "RT-ONLY4", Area = "MOD-L1", Buffer4 = "PC-T4-01" }, app: PullApp.Name));
        var nid = Call(h, "RT-ONLY4", CaseA);
        h.Read<LogisticRequest>(q => q.Where(r => r.NId == nid)).ShouldHaveSingleItem();
        h.Read<TransportOperation>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Record_checks_part_source_buffer_and_fefo_and_keeps_one_lot_per_task()
    {
        var (h, _) = await StackAsync();
        var task = TaskOf(h, Call(h, "RT-SEP", Sep)).NId;

        Fg.Error(Run(h, new RecordLot { TaskNId = "TW-NONE", MtuNId = "X" }, Mover)).ShouldBe(NvErrors.UnknownPullTask);
        Fg.Error(Run(h, new RecordLot { TaskNId = task, MtuNId = "X" }, Mover)).ShouldBe(ProductErrors.UnknownMtu);

        Lot(h, "LOT-WRONG", CaseB, "PC-T2-02");
        Fg.Error(Run(h, new RecordLot { TaskNId = task, MtuNId = "LOT-WRONG" }, Mover)).ShouldBe(NvErrors.LotWrongMaterial);

        Lot(h, "LOT-KHO", Sep, "RCV-R02");
        Fg.Error(Run(h, new RecordLot { TaskNId = task, MtuNId = "LOT-KHO" }, Mover)).ShouldBe(NvErrors.LotNotInSource);
        Ok(h.Run(new CreateMtu { NId = "LOT-FREE", MaterialNId = Sep, Quantity = 1 }));
        Fg.Error(Run(h, new RecordLot { TaskNId = task, MtuNId = "LOT-FREE" }, Mover)).ShouldBe(NvErrors.LotNotInSource);

        // FEFO tính trên buffer nguồn và buffer tier 1: lô ở kho hạn sớm hơn thì lô ở nguồn phải chờ.
        var label = DirectLabel.Build("D1", "PO1", Sep, "BATCHEXP", 100);
        Ok(Run(h, new ReceiveDirectDelivery { Payload = """[{"poNo":"PO1","poLine":"10","material":"NV-RM-SEP","batch":"BATCHEXP","quantity":100,"expirationDate":"20270101"}]""" }));
        Ok(Run(h, new ReceiveDirectMaterial { Label = label, StorageLocation = "NV1-CMN-RCV-R02", PalletNumber = "1", Quantity = 1 }, Mover));
        Lot(h, "LOT-SRC", Sep, "PC-T2-03");
        var outside = Run(h, new RecordLot { TaskNId = task, MtuNId = "LOT-SRC" }, Mover);
        Fg.Error(outside).ShouldBe(NvErrors.LotOutsideFefo);
        Fg.Message(outside).ShouldContain("BATCHEXP_1");

        // Không còn lô hạn sớm: lô cũ nhất đi trước.
        Ok(Run(h, new UnloadLot { MtuNId = "BATCHEXP_1" }, Line));
        Ok(Run(h, new UnloadLot { MtuNId = "LOT-KHO" }, Line));
        Ok(Run(h, new RecordLot { TaskNId = task, MtuNId = "LOT-SRC" }, Mover));
        Fg.Error(Run(h, new RecordLot { TaskNId = task, MtuNId = "LOT-SRC" }, Mover)).ShouldBe(NvErrors.LotAlreadyRecorded);
    }

    [Fact]
    public async Task Cancel_works_only_on_a_released_task_without_a_lot()
    {
        var (h, _) = await StackAsync();
        var open = TaskOf(h, Call(h, "RT-SEP", Sep)).NId;
        Ok(Run(h, new CancelPullTask { TaskNId = open }, Line));
        h.Read<TransportOperation>(q => q.Where(t => t.NId == open)).Single().Status.ShouldBe(TransportStatus.Cancelled);
        Fg.Error(Run(h, new CancelPullTask { TaskNId = open }, Line)).ShouldBe(ProductErrors.InvalidLogisticStatus);

        var accepted = TaskOf(h, Call(h, "RT-SEP", Sep)).NId;
        Ok(Run(h, new AcceptPullTask { TaskNId = accepted }, Mover));
        Fg.Error(Run(h, new CancelPullTask { TaskNId = accepted }, Line)).ShouldBe(ProductErrors.InvalidLogisticStatus);

        var withLot = TaskOf(h, Call(h, "RT-SEP", Sep)).NId;
        Ok(Run(h, new RecordLot { TaskNId = withLot, MtuNId = Lot(h, "LOT-C", Sep, "PC-T2-03") }, Mover));
        Fg.Error(Run(h, new CancelPullTask { TaskNId = withLot }, Line)).ShouldBe(NvErrors.InvalidPullTask);
    }

    [Theory]
    [InlineData(true, 10, 600, "Released", AndonStatus.HotCall)]
    [InlineData(true, 9000, 600, "Active", AndonStatus.HotCall)]
    [InlineData(false, 601, 600, "Released", AndonStatus.Behind)]
    [InlineData(false, 1, 0, "Released", AndonStatus.Behind)]
    [InlineData(false, 600, 600, "Active", AndonStatus.OnTime)]
    [InlineData(false, 0, 0, "Released", AndonStatus.OnTime)]
    [InlineData(false, 10, 600, "Released", AndonStatus.OnTime)]
    public void Andon_status_matrix_keeps_the_order_of_the_real_system(bool hot, double elapsed, int standard, string task, string expected) =>
        AndonStatus.Of(hot, elapsed, standard, task).ShouldBe(expected);

    [Fact]
    public async Task Andon_reads_kanban_calls_only_and_marks_every_row_from_the_selected_one()
    {
        var (h, _) = await StackAsync();
        var address = CaseA + "|NV1-CMN-RCV-R01";
        var plain = TaskOf(h, Call(h, "RT-CASE-A", CaseA, address: address)).NId;
        h.Time.Advance(TimeSpan.FromMinutes(61));
        var late = TaskOf(h, Call(h, "RT-CASE-A", CaseA, address: address)).NId;
        var hot = TaskOf(h, Call(h, "RT-CASE-A", CaseA, hot: true, address: address)).NId;
        Call(h, "RT-SEP", Sep);

        var routes = h.ReadFunction<AndonRoutes, NoPullParameters, AndonRow>(new NoPullParameters());
        routes.Select(r => r.TaskNId).ShouldBe([plain, late, hot], ignoreOrder: true);
        routes.ShouldAllBe(r => r.StandardSeconds == 3600);

        // Chọn dòng đã qua 61 phút: chuẩn 60 phút nên mọi dòng thường trễ, kể cả dòng vừa gọi.
        var fromPlain = Detail(h, plain);
        fromPlain[plain].ShouldBe(AndonStatus.Behind);
        fromPlain[late].ShouldBe(AndonStatus.Behind);
        fromPlain[hot].ShouldBe(AndonStatus.HotCall);

        // Chọn dòng vừa gọi: dòng cũ cũng thành đúng hạn.
        var fromLate = Detail(h, late);
        fromLate[plain].ShouldBe(AndonStatus.OnTime);
        fromLate[hot].ShouldBe(AndonStatus.HotCall);

        // Chọn dòng gấp: dòng thường trống, không dòng nào trễ.
        var fromHot = Detail(h, hot);
        fromHot[hot].ShouldBe(AndonStatus.HotCall);
        fromHot[plain].ShouldBeNull();
        fromHot[late].ShouldBeNull();
    }

    private static Dictionary<string, string?> Detail(SolutionHarness h, string task) =>
        h.ReadFunction<AndonDetail, AndonDetailParameters, AndonRow>(new AndonDetailParameters { TaskNId = task }).ToDictionary(r => r.TaskNId!, r => r.CallStatus);

    [Fact]
    public async Task Lead_time_goes_late_by_the_minutes_of_the_leg_and_delivered_shows_after_record()
    {
        var (h, _) = await StackAsync();
        var task = TaskOf(h, Call(h, "RT-CASE-A", CaseA)).NId;
        var row = () => h.ReadFunction<PullTasks, PullTaskParameters, PullTaskRow>(new PullTaskParameters { Area = "MOD-L1", Level = "4" }).Single(r => r.TaskNId == task);

        row().LeadTimeStatus.ShouldBe("InTime");
        h.Time.Advance(TimeSpan.FromMinutes(10));
        row().LeadTimeStatus.ShouldBe("Late");
        h.ReadFunction<PullTasks, PullTaskParameters, PullTaskRow>(new PullTaskParameters { Area = "MOD-L1", Level = "3" }).ShouldBeEmpty();

        row().Status.ShouldBe(TransportStatus.Released);
        Ok(Run(h, new RecordLot { TaskNId = task, MtuNId = Lot(h, "LOT-L", CaseA, "PC-T3-01") }, Mover));
        row().Status.ShouldBe("MaterialDelivered");
        Ok(Run(h, new CompletePullTask { TaskNId = task }, Mover));
        h.ReadFunction<PullTasks, PullTaskParameters, PullTaskRow>(new PullTaskParameters { Area = "MOD-L1", Level = "4" }).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tiles_list_kanban_parts_in_effect_and_direct_materials_for_the_production_area()
    {
        var (h, _) = await StackAsync();
        var tiles = () => h.ReadFunction<LinkedMaterials, PullEquipmentParameters, LinkedMaterialRow>(new PullEquipmentParameters { EquipmentNId = Area });
        tiles().Select(t => (t.MaterialNId, t.KanbanNo)).ShouldBe([(CaseA, "KB-CASE-A"), (CaseB, "KB-CASE-B"), (Sep, null)]);
        tiles().Single(t => t.MaterialNId == Sep).Quantity.ShouldBe(50);
        h.ReadFunction<LinkedMaterials, PullEquipmentParameters, LinkedMaterialRow>(new PullEquipmentParameters { EquipmentNId = AreaSub }).ShouldBeEmpty();

        // Kanban hết hạn: mất ô linh kiện, ô đặt thẳng vẫn còn. Part address chưa tới ngày cũng vậy.
        Ok(h.Run(new SetKanban { MaterialNId = CaseA, DockCode = "L1", SupplierCode = "SUP-1", KanbanNo = "KB-CASE-A", QuantityPerContainer = 300, EffectiveFrom = "2026-01-01", EffectiveTo = "2026-01-31" }, app: MapApp.Name));
        tiles().Select(t => t.MaterialNId).ShouldBe([CaseB, Sep]);
        Ok(h.Run(new SetPartAddress { MaterialNId = CaseB, StoreAddress = "NV1-CMN-RCV-R02", KanbanNId = "KB-CASE-B", DeliveryRouteNId = "RT-CASE-B", EffectiveFrom = "2027-01-01" }, app: MapApp.Name));
        tiles().Select(t => t.MaterialNId).ShouldBe([Sep]);
    }

    [Fact]
    public async Task Kanban_import_updates_by_key_and_refuses_bad_dates()
    {
        var (h, _) = await StackAsync();
        var before = h.Read<Kanban>().Count;
        var row = new { part = CaseA, dock = "L1", supplier = "SUP-1", kanban = "KB-NEW", quantity = 600m, effectiveFrom = "2026-01-01", effectiveTo = (string?)null };
        Ok(h.Run(new ImportKanbans { Payload = JsonSerializer.Serialize(new[] { row, row with { dock = "L2" } }) }, app: MapApp.Name));
        h.Read<Kanban>().Count.ShouldBe(before + 1);
        h.Read<Kanban>(q => q.Where(k => k.DockCode == "L1" && k.MaterialNId == CaseA)).Single().KanbanNo.ShouldBe("KB-NEW");

        Fg.Error(h.Run(new ImportKanbans { Payload = JsonSerializer.Serialize(new[] { row with { dock = "L3" }, row with { dock = "L4", effectiveFrom = "2026-01-01T00:00:00" } }) }, app: MapApp.Name)).ShouldBe(NvErrors.InvalidKanban);
        h.Read<Kanban>().Count.ShouldBe(before + 1);
        Fg.Error(h.Run(new ImportKanbans { Payload = "not json" }, app: MapApp.Name)).ShouldBe(NvErrors.InvalidKanban);
    }

    [Fact]
    public async Task Part_address_screen_tells_the_supplier_portal_and_the_suite_command_does_not()
    {
        var (h, gateway) = await StackAsync();
        Ok(h.Run(new SetPartAddress { MaterialNId = CaseA, StoreAddress = "NV1-CMN-RCV-R02" }, app: MapApp.Name));
        gateway.Posted.ShouldBeEmpty();

        Ok(h.Run(new SavePartAddress { MaterialNId = CaseA, StoreAddress = "NV1-CMN-RCV-R02", KanbanNId = "KB-CASE-A" }, app: MapApp.Name));
        Ok(h.Run(new SavePartAddress { MaterialNId = CaseB, StoreAddress = "NV1-CMN-RCV-R01" }, app: MapApp.Name));
        Ok(h.Run(new DeletePartAddress { NId = CaseB + "|NV1-CMN-RCV-R01" }, app: MapApp.Name));
        gateway.Posted.Select(m => (m.Adapter, m.MessageType)).ShouldBe([
            (MapAdapters.SupplierPortal, "PartAddressUpdate"), (MapAdapters.SupplierPortal, "PartAddressAdd"), (MapAdapters.SupplierPortal, "PartAddressDelete"),
        ]);
        Fg.Error(h.Run(new DeletePartAddress { NId = "NONE" }, app: MapApp.Name)).ShouldBe(NvErrors.UnknownPartAddress);
    }

    [Fact]
    public async Task Direct_material_converts_units_takes_expiry_links_the_buffer_and_blocks_over_receiving()
    {
        var (h, _) = await StackAsync();
        Ok(Run(h, new ReceiveDirectDelivery { Payload = """[{"poNo":"PO-7","poLine":"10","material":"NV-RM-SEP","batch":"BATCH00001","quantity":10,"expirationDate":"20270407"}]""" }));
        var label = DirectLabel.Build("DEL0000001", "PO-7", Sep, "BATCH00001", 10);

        var result = Run(h, new ReceiveDirectMaterial { Label = label, StorageLocation = "NV1-CMN-RCV-R02", PalletNumber = "1", Quantity = 6 }, Mover);
        Ok(result);
        var lot = h.Read<Mtu>(q => q.Where(m => m.NId == "BATCH00001_1")).Single();

        // Hệ Receiving: 1 đơn vị nhận = 10 m.
        (lot.Quantity, lot.EquipmentNId, lot.Code).ShouldBe((60m, "NV1-CMN-RCV-R02", "BATCH00001_1"));
        BufferOfLot(h, lot.NId).ShouldBe("RCV-R02");
        var expiry = h.Read<LotExpiry>(q => q.Where(e => e.NId == lot.NId)).Single();
        (expiry.ExpirationDate, expiry.PoNo).ShouldBe(("20270407", "PO-7"));

        var over = Run(h, new ReceiveDirectMaterial { Label = label, StorageLocation = "NV1-CMN-RCV-R02", PalletNumber = "2", Quantity = 5 }, Mover);
        Fg.Error(over).ShouldBe(NvErrors.OverReceive);
        Fg.Message(over).ShouldContain("Already received: 6");
        Ok(Run(h, new ReceiveDirectMaterial { Label = label, StorageLocation = "NV1-CMN-RCV-R02", PalletNumber = "2", Quantity = 4 }, Mover));

        Fg.Error(Run(h, new ReceiveDirectMaterial { Label = label[..30], StorageLocation = "NV1-CMN-RCV-R02", PalletNumber = "3", Quantity = 1 }, Mover)).ShouldBe(NvErrors.InvalidDirectLabel);
        Fg.Error(Run(h, new ReceiveDirectMaterial { Label = label, StorageLocation = "NONE", PalletNumber = "3", Quantity = 1 }, Mover)).ShouldBe(NvErrors.UnknownLocation);
        Fg.Error(Run(h, new ReceiveDirectMaterial { Label = DirectLabel.Build("D", "P", "NOPE", "B2", 1), StorageLocation = "NV1-CMN-RCV-R02", PalletNumber = "1", Quantity = 1 }, Mover)).ShouldBe(NvErrors.UnknownMaterial);
    }

    [Fact]
    public async Task Direct_material_without_an_order_has_no_expiry_and_goes_after_lots_with_one()
    {
        var (h, _) = await StackAsync();
        var label = DirectLabel.Build("D", "P", Sep, "NOORDER", 5);
        Ok(Run(h, new ReceiveDirectMaterial { Label = label, StorageLocation = "NV1-CMN-RCV-R02", PalletNumber = "1", Quantity = 1 }, Mover));
        h.Read<LotExpiry>(q => q.Where(e => e.NId == "NOORDER_1")).Single().ExpirationDate.ShouldBeNull();
        var task = TaskOf(h, Call(h, "RT-SEP", Sep)).NId;
        var scan = h.ReadFunction<ScanLot, ScanParameters, ScannedLotRow>(new ScanParameters { TaskNId = task, Label = label }).ShouldHaveSingleItem();
        (scan.MtuNId, scan.IsInSourceBuffer, scan.IsFefo, scan.CurrentBufferNId).ShouldBe(("NOORDER_1", false, true, "RCV-R02"));
    }

    [Fact]
    public async Task Scan_finds_a_received_skid_by_its_label_only_while_it_belongs_to_a_buffer()
    {
        var (h, _) = await StackAsync();
        var task = TaskOf(h, Call(h, "RT-SEP", Sep)).NId;
        Lot(h, "SUP-1L1PO00000001F001", CaseA, "RCV-R01");
        var label = SkidLabel.Build("SUP-1", "L1", "PO00000001", "RTEA01", "F", "001");
        var scan = () => h.ReadFunction<ScanLot, ScanParameters, ScannedLotRow>(new ScanParameters { TaskNId = task, Label = label });
        scan().ShouldHaveSingleItem().MtuNId.ShouldBe("SUP-1L1PO00000001F001");

        Ok(Run(h, new UnloadLot { MtuNId = "SUP-1L1PO00000001F001" }, Line));
        scan().ShouldBeEmpty();
        h.ReadFunction<SearchLots, LabelParameters, Mtu>(new LabelParameters { Label = label }).ShouldHaveSingleItem();
        h.ReadFunction<SearchLots, LabelParameters, Mtu>(new LabelParameters { Label = string.Empty }).ShouldBeEmpty();
    }

    [Fact]
    public async Task Lab_L20_load_is_refused_while_the_lot_is_still_in_the_delivery_cell_buffer()
    {
        var (h, _) = await StackAsync();
        var lot = Lot(h, "LOT-L20", CaseA, "PC-T3-01");
        Finish(h, TaskOf(h, Call(h, "RT-CASE-A", CaseA)).NId, lot);
        BufferOfLot(h, lot).ShouldBe("PC-T4-01");

        // Khu cấp Area không có buffer; subarea có nhưng lô còn thuộc ô tier 4: lệnh báo đúng buffer đang giữ.
        Fg.Error(Run(h, new LoadLot { EquipmentNId = Area, MtuNId = lot }, Line)).ShouldBe(NvErrors.NoBufferOnEquipment);
        var refused = Run(h, new LoadLot { EquipmentNId = AreaSub, MtuNId = lot }, Line);
        Fg.Error(refused).ShouldBe(ProductErrors.AlreadyInBuffer);
        Fg.Message(refused).ShouldContain("PC-T4-01");
        EquipmentOf(h, lot).ShouldBe("NV1-CMN-PC-T4-01");

        // Gỡ không đổi vị trí; nạp lại thì đổi cả buffer lẫn vị trí.
        Ok(Run(h, new UnloadLot { MtuNId = lot }, Line));
        EquipmentOf(h, lot).ShouldBe("NV1-CMN-PC-T4-01");
        Ok(Run(h, new LoadLot { EquipmentNId = AreaSub, MtuNId = lot }, Line));
        BufferOfLot(h, lot).ShouldBe(AreaSub + "_Buffer");
        EquipmentOf(h, lot).ShouldBe(AreaSub);
        h.ReadFunction<LoadedLots, PullEquipmentParameters, Mtu>(new PullEquipmentParameters { EquipmentNId = AreaSub }).ShouldHaveSingleItem().NId.ShouldBe(lot);
    }

    [Fact]
    public async Task Load_does_not_check_the_whitelist()
    {
        var (h, _) = await StackAsync();
        Ok(h.Run(new CreateMtu { NId = "LOT-W", MaterialNId = Sep, Quantity = 1, EquipmentNId = "NV1-CMN-RCV-R02" }));
        Ok(Run(h, new LoadLot { EquipmentNId = AreaSub, MtuNId = "LOT-W" }, Line));
        Fg.Error(h.Run(new AttachMtuToBuffer { BufferNId = AreaSub + "_Buffer", MtuNId = "LOT-W" })).ShouldBe(ProductErrors.AlreadyInBuffer);
    }

    [Fact]
    public async Task Receiving_a_skid_puts_it_in_the_buffer_of_its_store_so_it_can_be_pulled()
    {
        var (h, _) = await StackAsync();
        Ok(h.Yard(new CreateTrailer { Barcode = "BC-P", TrailerNo = "T-P" }));
        var payload = JsonSerializer.Serialize(new
        {
            route = "RTEP",
            trailerNo = "T-P",
            plannedOn = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero),
            skids = new[] { new { poNo = "PO00000009", supplier = "SUP-1", dock = "L1", pallet = "F", skid = "001", material = CaseA, kanban = "KB-CASE-A", quantityPerBox = 6m, boxCount = 50m } },
        });
        var schedule = h.Yard(new ReceiveDeliverySchedule { Payload = payload });
        Ok(schedule);
        Ok(h.Yard(new ArriveComponentTrailer { ScheduleNId = schedule.Body["NId"]!.GetValue<string>(), Barcode = "BC-P", LocationNId = "NV1-CMN-YARD-Y01" }));
        Ok(h.Yard(new CreateTrailerMovementRequest { Barcode = "BC-P", DestinationNId = "NV1-CMN-DOCK-D01" }));
        var move = h.Read<TrailerMovement>().Single();
        foreach (var step in new[] { MovementStates.Accepted, MovementStates.Picked, MovementStates.Dropped })
        {
            Ok(h.Yard(new UpdateTrailerMovement { MovementNId = move.NId, Status = step }));
        }

        Ok(h.Yard(new ReceiveSkid { Barcode = "BC-P", SkidBarcode = SkidLabel.Build("SUP-1", "L1", "PO00000009", "RTEP", "F", "001"), StorageLocation = "NV1-CMN-RCV-R01" }));
        BufferOfLot(h, "SUP-1L1PO00000009F001").ShouldBe("RCV-R01");
    }

    [Fact]
    public async Task Roles_split_calling_moving_and_master_data()
    {
        var (h, _) = await StackAsync();
        Run(h, new CallMaterial { RouteNId = "RT-SEP", MaterialNId = Sep, Quantity = 1 }, Mover).StatusCode.ShouldBe(403);
        var task = TaskOf(h, Call(h, "RT-SEP", Sep)).NId;
        Run(h, new AcceptPullTask { TaskNId = task }, Line).StatusCode.ShouldBe(403);
        Run(h, new SetDeliveryRoute { NId = "X", Buffer4 = "PC-T4-01" }, Mover).StatusCode.ShouldBe(403);
        Run(h, new ReceiveDirectDelivery { Payload = "[]" }, Mover).StatusCode.ShouldBe(403);
    }
}

internal static class PullReads
{
    /// <summary>Gọi reading function trên bản đã commit, cùng đồng hồ của harness.</summary>
    public static List<TRow> ReadFunction<TFunction, TParameters, TRow>(this SolutionHarness h, TParameters parameters)
        where TFunction : IReadingFunction<TParameters, TRow>, new()
    {
        using var session = h.Executor.OpenRead();
        return [.. new TFunction().Read(parameters, new Reader(session, h.Time))];
    }

    private sealed class Reader(ReadSession session, TimeProvider time) : IReadingPlatform
    {
        public CallerIdentity Caller => CallerIdentity.System;

        public TimeProvider Time => time;

        public IQueryable<T> ProjectionQuery<T>()
            where T : class => session.SetOf<T>();
    }
}
