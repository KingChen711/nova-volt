using System.Text.Json;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Product;
using Nvm.Project;

namespace Nvm.SolutionTests;

/// <summary>
/// R9: bốn luồng (xe linh kiện, xe rỗng, xe không lịch, chuyển skid) chạy trọn, trạng thái lịch và trailer đúng sau từng bước; nhà cung
/// cấp tại chỗ tự nhận; lab L19 (nhãn lệch một ký tự, số đơn dài hơn ô của nó); skid đã nhận không chuyển được.
/// </summary>
public sealed class InboundTrailerTests
{
    private const string Dock1 = "NV1-CMN-DOCK-D01";
    private const string Dock2 = "NV1-CMN-DOCK-D02";
    private const string DockOtherLogical = "NV1-CMN-DOCK-D03";
    private const string DirectDock = "NV1-CMN-DOCK-D07";
    private const string EmptyDock = "NV1-CMN-DOCK-D08";
    private const string Yard1 = "NV1-CMN-YARD-Y01";
    private const string Yard2 = "NV1-CMN-YARD-Y02";
    private const string Store = "NV1-CMN-RCV-R01";
    private const string Part = "NV-RM-CASE-A";
    private const string Kanban = "KB-CASE-A";

    private static readonly CallerIdentity Gate = SolutionHarness.Caller("yard.nv1", "yard");
    private static readonly DateTimeOffset Planned = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    private static string Label(string po, string route, string skid, string supplier = "SUP-1", string dock = "L1", string pallet = "F") =>
        SkidLabel.Build(supplier, dock, po, route, pallet, skid);

    private static string Schedule(SolutionHarness h, string route, string? trailerNo, string po = "PO00000001", string supplier = "SUP-1", string material = Part, string kanban = Kanban, DateTimeOffset? on = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            route,
            trailerNo,
            plannedOn = on ?? Planned,
            skids = new[]
            {
                new { poNo = po, supplier, dock = "L1", pallet = "F", skid = "001", material, kanban, quantityPerBox = 6m, boxCount = 50m },
                new { poNo = po, supplier, dock = "L1", pallet = "F", skid = "002", material, kanban, quantityPerBox = 6m, boxCount = 50m },
            },
        });
        var result = h.Yard(new ReceiveDeliverySchedule { Payload = payload });
        Ok(result);
        return result.Body["NId"]!.GetValue<string>();
    }

    private static void Ok(CommandResult result) => result.StatusCode.ShouldBe(200, Fg.Message(result));

    private static Trailer Get(SolutionHarness h, string barcode) => h.Read<Trailer>(q => q.Where(t => t.NId == barcode)).Single();

    private static InboundSchedule Sched(SolutionHarness h, string nid) => h.Read<InboundSchedule>(q => q.Where(s => s.NId == nid)).Single();

    private static void NewTrailer(SolutionHarness h, string barcode, string no) => Ok(h.Yard(new CreateTrailer { Barcode = barcode, TrailerNo = no }, Gate));

    private static void Pull(SolutionHarness h, string barcode, string destination, CallerIdentity? caller = null)
    {
        var request = h.Read<TrailerMovement>(q => q.Where(m => m.TrailerNId == barcode && m.Status != MovementStates.Dropped)).SingleOrDefault();
        if (request is null)
        {
            Ok(h.Yard(new CreateTrailerMovementRequest { Barcode = barcode, DestinationNId = destination }, caller ?? Gate));
            request = h.Read<TrailerMovement>(q => q.Where(m => m.TrailerNId == barcode && m.Status != MovementStates.Dropped)).Single();
        }

        foreach (var step in new[] { MovementStates.Accepted, MovementStates.Picked, MovementStates.Dropped })
        {
            Ok(h.Yard(new UpdateTrailerMovement { MovementNId = request.NId, Status = step }, caller ?? Gate));
        }
    }

    private static string AtDock(SolutionHarness h, string barcode, string no, string route, string po, string dock, DateTimeOffset? on = null)
    {
        var nid = Schedule(h, route, no, po, on: on);
        NewTrailer(h, barcode, no);
        Ok(h.Yard(new ArriveComponentTrailer { ScheduleNId = nid, Barcode = barcode, LocationNId = FreeYard(h) }, Gate));
        Pull(h, barcode, dock);
        return nid;
    }

    private static string FreeYard(SolutionHarness h)
    {
        var used = h.Read<Trailer>().Select(t => t.LocationNId).ToHashSet();
        return Enumerable.Range(1, 12).Select(i => $"NV1-CMN-YARD-Y{i:00}").First(y => !used.Contains(y));
    }

    [Fact]
    public async Task Component_trailer_runs_from_schedule_through_receiving_to_reload_and_departure()
    {
        var h = await Fg.StackAsync();
        var nid = Schedule(h, "RTEA01", "T-100");
        (Sched(h, nid).GateStatus, Sched(h, nid).LoadingStatus).ShouldBe((GateStates.InTransit, LoadingStates.ToArrive));
        h.Read<InboundSkid>().Count(s => s.ScheduleNId == nid).ShouldBe(2);

        // Sai xe, ô không phải yard: bị từ chối, lịch không đổi.
        NewTrailer(h, "BC-100", "T-100");
        NewTrailer(h, "BC-999", "T-999");
        Fg.Error(h.Yard(new ArriveComponentTrailer { ScheduleNId = nid, Barcode = "BC-999", LocationNId = Yard1 }, Gate)).ShouldBe(NvErrors.WrongTrailer);
        Fg.Error(h.Yard(new ArriveComponentTrailer { ScheduleNId = nid, Barcode = "BC-100", LocationNId = Dock1 }, Gate)).ShouldBe(NvErrors.UnknownLocation);

        Ok(h.Yard(new ArriveComponentTrailer { ScheduleNId = nid, Barcode = "BC-100", LocationNId = Yard1 }, Gate));
        var arrived = Get(h, "BC-100");
        (arrived.Status, arrived.ContentType, arrived.LocationNId).ShouldBe((TrailerStates.Full, TrailerContents.Component, Yard1));
        arrived.PlacardNId!.ShouldStartWith("PL-C");
        (Sched(h, nid).GateStatus, Sched(h, nid).LoadingStatus).ShouldBe((GateStates.Arrived, LoadingStates.ToBeUnloaded));

        // Nhận khi xe còn ở yard: bị từ chối.
        var first = Label("PO00000001", "RTEA01", "001");
        var second = Label("PO00000001", "RTEA01", "002");
        Fg.Error(h.Yard(new ReceiveSkid { Barcode = "BC-100", SkidBarcode = first, StorageLocation = "RC-01" }, Gate)).ShouldBe(NvErrors.TrailerNotAtDock);

        // Yêu cầu kéo xe: vị trí chỉ đổi khi Dropped.
        Ok(h.Yard(new CreateTrailerMovementRequest { Barcode = "BC-100", DestinationNId = Dock1 }, Gate));
        Get(h, "BC-100").LocationNId.ShouldBe(Yard1);
        Pull(h, "BC-100", Dock1);
        Get(h, "BC-100").LocationNId.ShouldBe(Dock1);

        // Vị trí khác mặc định phải xác nhận; mã phụ của vị trí dùng được thay tên.
        Fg.Error(h.Yard(new ReceiveSkid { Barcode = "BC-100", SkidBarcode = first, StorageLocation = "RC-02" }, Gate)).ShouldBe(NvErrors.NotDefaultStorage);
        Fg.Error(h.Yard(new ReceiveSkid { Barcode = "BC-100", SkidBarcode = first, StorageLocation = "KHONG-CO" }, Gate)).ShouldBe(NvErrors.UnknownLocation);
        Ok(h.Yard(new ReceiveSkid { Barcode = "BC-100", SkidBarcode = first, StorageLocation = "RC-01" }, Gate));
        Get(h, "BC-100").Status.ShouldBe(TrailerStates.Tapped);
        Sched(h, nid).LoadingStatus.ShouldBe(LoadingStates.Unloading);
        var mtu = h.Read<Mtu>(q => q.Where(m => m.NId == "SUP-1L1PO00000001F001")).Single();
        (mtu.MaterialNId, mtu.Quantity, mtu.EquipmentNId).ShouldBe((Part, 300m, Store));
        Fg.Error(h.Yard(new ReceiveSkid { Barcode = "BC-100", SkidBarcode = first, StorageLocation = "RC-01" }, Gate)).ShouldBe(NvErrors.SkidAlreadyReceived);

        // Còn skid: phải xác nhận dỡ thiếu; nhận đủ thì không cần.
        Fg.Error(h.Yard(new CompleteUnloading { Barcode = "BC-100" }, Gate)).ShouldBe(NvErrors.SkidsPending);
        Ok(h.Yard(new ReceiveSkid { Barcode = "BC-100", SkidBarcode = second, StorageLocation = Store }, Gate));
        Ok(h.Yard(new CompleteUnloading { Barcode = "BC-100" }, Gate));
        Get(h, "BC-100").Status.ShouldBe(TrailerStates.Empty);
        Sched(h, nid).LoadingStatus.ShouldBe(LoadingStates.Unloaded);

        // Chất hàng trả: phần trăm phải dương, placard đúng route.
        var placard = h.Yard(new GeneratePlacard { Type = PlacardTypes.ReturnComponent, Route = "RET1" }, Gate).Body["NId"]!.GetValue<string>();
        placard.ShouldStartWith("PL-R");
        Fg.Error(h.Yard(new ReportTrailerReloaded { Barcode = "BC-100", ReturnRoute = "RET1", PlacardNId = placard, Percent = 0m, DestinationNId = Yard2 }, Gate)).ShouldBe(NvErrors.InvalidReload);
        Fg.Error(h.Yard(new ReportTrailerReloaded { Barcode = "BC-100", ReturnRoute = "RET2", PlacardNId = placard, Percent = 60m, DestinationNId = Yard2 }, Gate)).ShouldBe(NvErrors.InvalidPlacard);
        Ok(h.Yard(new ReportTrailerReloaded { Barcode = "BC-100", ReturnRoute = "RET1", PlacardNId = placard, Percent = 60m, DestinationNId = Yard2 }, Gate));
        var reloaded = Get(h, "BC-100");
        (reloaded.Status, reloaded.ContentType, reloaded.Route, reloaded.PlacardNId).ShouldBe((TrailerStates.Full, TrailerContents.ComponentReturn, "RET1", placard));
        Sched(h, nid).GateStatus.ShouldBe(GateStates.CompletedReloaded);
        var ret = h.Read<InboundSchedule>(q => q.Where(s => s.TrailerNId == "BC-100" && s.GateStatus == GateStates.ForDeparture)).Single();
        (ret.ScheduleType, ret.MaterialType, ret.LoadingStatus, ret.PercentReloaded).ShouldBe((ScheduleTypes.Loading, TrailerContents.ComponentReturn, LoadingStates.Loaded, 60m));

        // Xe rời: chưa ra yard, chưa có yard pass đều bị chặn.
        Fg.Error(h.Yard(new DepartTrailer { Barcode = "BC-100" }, Gate)).ShouldBe(NvErrors.MovementOpen);
        Pull(h, "BC-100", Yard2);
        Fg.Error(h.Yard(new DepartTrailer { Barcode = "BC-100", YardPassNId = "YP-SAI" }, Gate)).ShouldBe(NvErrors.NoYardPass);
        var pass = h.Yard(new IssueTrailerYardPass { Barcode = "BC-100" }, Gate).Body["NId"]!.GetValue<string>();
        Ok(h.Yard(new DepartTrailer { Barcode = "BC-100", YardPassNId = pass }, Gate));
        var gone = Get(h, "BC-100");
        (gone.Status, gone.LocationNId, gone.PlacardNId, gone.YardPassNId, gone.ContentType).ShouldBe((TrailerStates.Free, null, null, null, null));
        Sched(h, ret.NId).GateStatus.ShouldBe(GateStates.Departed);
        h.Read<TrailerPlacard>(q => q.Where(p => p.NId == placard)).Single().TrailerNId.ShouldBeNull();

        // Xe Free đăng ký lại được cho chuyến sau.
        var next = Schedule(h, "RTEA02", "T-100", "PO00000002", on: Planned.AddHours(1));
        Ok(h.Yard(new ArriveComponentTrailer { ScheduleNId = next, Barcode = "BC-100", LocationNId = Yard1 }, Gate));
    }

    [Fact]
    public async Task Short_unloading_needs_confirmation_and_mails_the_unreceived_skids()
    {
        var h = await Fg.StackAsync();
        var nid = AtDock(h, "BC-S", "T-S", "RTES", "PO00000003", Dock1);
        Ok(h.Yard(new CompleteUnloading { Barcode = "BC-S", ConfirmMissing = true }, Gate));
        Get(h, "BC-S").Status.ShouldBe(TrailerStates.Empty);
        Sched(h, nid).LoadingStatus.ShouldBe(LoadingStates.Unloaded);
        h.Read<InboundSkid>(q => q.Where(s => s.ScheduleNId == nid)).ShouldAllBe(s => !s.Verified);
    }

    [Fact]
    public async Task Empty_trailer_is_called_to_a_dock_reloaded_moved_out_and_departs_with_a_yard_pass()
    {
        var h = await Fg.StackAsync();
        NewTrailer(h, "BC-E", "T-E");
        Ok(h.Yard(new ArriveEmptyTrailer { Barcode = "BC-E", LocationNId = Yard1 }, Gate));
        Get(h, "BC-E").PlacardNId!.ShouldStartWith("PL-E");

        Fg.Error(h.Yard(new CallEmptyTrailer { Barcode = "BC-E", DockNId = Yard2 }, Gate)).ShouldBe(NvErrors.TrailerNotAtDock);
        Ok(h.Yard(new CallEmptyTrailer { Barcode = "BC-E", DockNId = Dock1 }, Gate));
        var loading = h.Read<InboundSchedule>(q => q.Where(s => s.TrailerNId == "BC-E")).Single();
        (loading.ScheduleType, loading.MaterialType, loading.GateStatus, loading.LoadingStatus).ShouldBe((ScheduleTypes.Loading, "Empty", GateStates.Created, LoadingStates.ToBeLoaded));
        Get(h, "BC-E").LocationNId.ShouldBe(Yard1);
        Pull(h, "BC-E", Dock1);

        var placard = h.Yard(new GeneratePlacard { Type = PlacardTypes.ReturnComponent, Route = "RET9" }, Gate).Body["NId"]!.GetValue<string>();
        Ok(h.Yard(new ReportTrailerReloaded { Barcode = "BC-E", ReturnRoute = "RET9", PlacardNId = placard, Percent = 100m, DestinationNId = Yard2 }, Gate));
        Get(h, "BC-E").Status.ShouldBe(TrailerStates.Full);
        Sched(h, loading.NId).GateStatus.ShouldBe(GateStates.CompletedReloaded);
        Pull(h, "BC-E", Yard2);
        var pass = h.Yard(new IssueTrailerYardPass { Barcode = "BC-E" }, Gate).Body["NId"]!.GetValue<string>();
        Ok(h.Yard(new DepartTrailer { Barcode = "BC-E", YardPassNId = pass }, Gate));
        Get(h, "BC-E").Status.ShouldBe(TrailerStates.Free);
    }

    [Fact]
    public async Task Unplanned_direct_material_trailer_goes_straight_to_its_dock_and_is_deleted_on_departure_without_a_yard_pass()
    {
        var h = await Fg.StackAsync();
        var type = UnplannedTypes.DirectMaterial;
        Fg.Error(h.Yard(new RegisterUnplannedTrailer { TrailerType = type, Trailer = "PLATE-1", LocationNId = DirectDock }, Gate)).ShouldBe(NvErrors.InvalidUnplannedType);
        Fg.Error(h.Yard(new RegisterUnplannedTrailer { TrailerType = type, Trailer = "PLATE-1", Carrier = "C", LocationNId = Dock1 }, Gate)).ShouldBe(NvErrors.UnknownLocation);
        Ok(h.Yard(new RegisterUnplannedTrailer { TrailerType = type, Trailer = "PLATE-1", Carrier = "C", LocationNId = DirectDock }, Gate));

        var trailer = Get(h, "PLATE-1");
        (trailer.Status, trailer.ContentType, trailer.LocationNId, trailer.Carrier).ShouldBe((TrailerStates.Full, type, DirectDock, "C"));
        h.Read<TrailerMovement>(q => q.Where(m => m.TrailerNId == "PLATE-1")).ShouldBeEmpty();
        var schedule = h.Read<InboundSchedule>(q => q.Where(s => s.TrailerNId == "PLATE-1")).Single();
        (schedule.Unplanned, schedule.GateStatus, schedule.LoadingStatus).ShouldBe((true, GateStates.Arrived, LoadingStates.ToBeUnloaded));
        Fg.Error(h.Yard(new RegisterUnplannedTrailer { TrailerType = type, Trailer = "PLATE-1", Carrier = "C", LocationNId = DirectDock }, Gate)).ShouldBe(NvErrors.InvalidTrailerState);

        Ok(h.Yard(new DepartTrailer { Barcode = "PLATE-1" }, Gate));
        (Get(h, "PLATE-1").Status, Get(h, "PLATE-1").LocationNId).ShouldBe((TrailerStates.Deleted, null));
        Sched(h, schedule.NId).GateStatus.ShouldBe(GateStates.Departed);
        Sched(h, schedule.NId).LoadingStatus.ShouldBe(LoadingStates.ToBeUnloaded);

        // Cửa được trả lại; biển số đã rời đăng ký lại được.
        Ok(h.Yard(new RegisterUnplannedTrailer { TrailerType = type, Trailer = "PLATE-1", Carrier = "C", LocationNId = DirectDock }, Gate));
    }

    [Fact]
    public async Task Other_unplanned_types_follow_their_rules()
    {
        var h = await Fg.StackAsync();
        Ok(h.Yard(new RegisterUnplannedTrailer { TrailerType = UnplannedTypes.PipedDirectMaterial, Trailer = "PIPE-1", Carrier = "C" }, Gate));
        (Get(h, "PIPE-1").Status, Get(h, "PIPE-1").LocationNId).ShouldBe((TrailerStates.Full, null));

        Fg.Error(h.Yard(new RegisterUnplannedTrailer { TrailerType = UnplannedTypes.CustomerEmpty, Trailer = "NOPE", LocationNId = EmptyDock }, Gate)).ShouldBe(NvErrors.UnknownTrailer);
        NewTrailer(h, "BC-CE", "T-CE");
        Fg.Error(h.Yard(new RegisterUnplannedTrailer { TrailerType = UnplannedTypes.CustomerEmpty, Trailer = "BC-CE", LocationNId = EmptyDock, PlacardNId = "PL-E-NONE" }, Gate)).ShouldBe(NvErrors.InvalidPlacard);
        var empty = h.Yard(new GeneratePlacard { Type = PlacardTypes.Empty }, Gate).Body["NId"]!.GetValue<string>();
        Ok(h.Yard(new RegisterUnplannedTrailer { TrailerType = UnplannedTypes.CustomerEmpty, Trailer = "BC-CE", LocationNId = EmptyDock, PlacardNId = empty }, Gate));
        Get(h, "BC-CE").Status.ShouldBe(TrailerStates.CustomerEmpty);

        NewTrailer(h, "BC-RG", "T-RG");
        var goods = h.Yard(new GeneratePlacard { Type = PlacardTypes.ReturnGoods, Route = "RG1" }, Gate).Body["NId"]!.GetValue<string>();
        Fg.Error(h.Yard(new RegisterUnplannedTrailer { TrailerType = UnplannedTypes.ReturnedGoods, Trailer = "BC-RG", LocationNId = Yard1, PlacardNId = goods, Route = "KHAC" }, Gate)).ShouldBe(NvErrors.InvalidPlacard);
        Ok(h.Yard(new RegisterUnplannedTrailer { TrailerType = UnplannedTypes.ReturnedGoods, Trailer = "BC-RG", LocationNId = Yard1, PlacardNId = goods, Route = "RG1" }, Gate));
        (Get(h, "BC-RG").Status, Get(h, "BC-RG").ContentType).ShouldBe((TrailerStates.FullReturned, TrailerContents.ReturnedGoods));

        Fg.Error(h.Yard(new RegisterUnplannedTrailer { TrailerType = "Khac", Trailer = "X", Carrier = "C" }, Gate)).ShouldBe(NvErrors.InvalidUnplannedType);
    }

    [Fact]
    public async Task Transfer_moves_unreceived_skids_keeps_their_label_and_empties_the_source()
    {
        var h = await Fg.StackAsync();
        var sourceSchedule = AtDock(h, "BC-SRC", "T-SRC", "RTSRC", "PO00000010", Dock1);
        AtDock(h, "BC-DST", "T-DST", "RTDST", "PO00000011", Dock2, Planned.AddHours(1));
        var one = Label("PO00000010", "RTSRC", "001");
        var two = Label("PO00000010", "RTSRC", "002");
        var transfer = new TransferSkids { SourceBarcode = "BC-SRC", DestinationBarcode = "BC-DST", ReasonNId = "Consolidate", Comments = "gop xe", SkidBarcodes = [one, two] };

        Fg.Error(h.Yard(new TransferSkids { SourceBarcode = "BC-SRC", DestinationBarcode = "BC-SRC", ReasonNId = "Consolidate", Comments = "x", SkidBarcodes = [one] }, Gate)).ShouldBe(NvErrors.InvalidTransfer);
        Fg.Error(h.Yard(new TransferSkids { SourceBarcode = "BC-SRC", DestinationBarcode = "BC-DST", ReasonNId = "KhongCo", Comments = "x", SkidBarcodes = [one] }, Gate)).ShouldBe(NvErrors.InvalidTransfer);
        Fg.Error(h.Yard(new TransferSkids { SourceBarcode = "BC-SRC", DestinationBarcode = "BC-DST", ReasonNId = "Consolidate", Comments = "x", SkidBarcodes = [one[..39]] }, Gate)).ShouldBe(NvErrors.InvalidSkidLabel);
        Fg.Error(h.Yard(new CheckTransferSkid { SourceBarcode = "BC-SRC", SkidBarcode = Label("PO00000011", "RTDST", "001") }, Gate)).ShouldBe(NvErrors.SkidNotOnSource);
        Ok(h.Yard(new CheckTransferSkid { SourceBarcode = "BC-SRC", SkidBarcode = one }, Gate));

        Ok(h.Yard(transfer, Gate));

        var source = Get(h, "BC-SRC");
        (source.Status, source.ContentType, source.Route, source.LocationNId).ShouldBe((TrailerStates.Empty, null, null, Dock1));
        source.LastComment!.ShouldContain("BC-DST");
        Sched(h, sourceSchedule).GateStatus.ShouldBe(GateStates.CompletedReloaded);
        h.Read<InboundSchedule>(q => q.Where(s => s.TrailerNId == "BC-SRC" && s.LoadingStatus == LoadingStates.ToBeLoaded)).Count.ShouldBe(1);

        var destination = Get(h, "BC-DST");
        (destination.Status, destination.Route).ShouldBe((TrailerStates.Full, "RTDST,RTSRC"));
        var added = h.Read<InboundSchedule>(q => q.Where(s => s.TrailerNId == "BC-DST" && s.Route == "RTSRC")).Single();
        (added.WithTransferredSkids, added.GateStatus, added.LoadingStatus).ShouldBe((true, GateStates.Arrived, LoadingStates.ToBeUnloaded));

        var moved = h.Read<InboundSkid>(q => q.Where(s => s.ScheduleNId == added.NId)).OrderBy(s => s.SkidNo).ToList();
        moved.Count.ShouldBe(2);
        moved.ShouldAllBe(s => s.Route == "RTSRC" && !s.Verified);
        var history = h.Read<SkidTransload>(q => q.Where(t => t.SkidNId == moved[0].NId)).OrderBy(t => t.Kind).ToList();
        history.Select(t => t.Kind).ShouldBe(["Arrival", "Transload"]);
        (history[1].FromTrailerNId, history[1].ToTrailerNId, history[1].ReasonNId, history[1].OldRoute).ShouldBe(("BC-SRC", "BC-DST", "Consolidate", "RTSRC"));

        // Nhãn cũ vẫn nhận được trên xe đích; xe đích nhận cả skid của route mình.
        Ok(h.Yard(new ReceiveSkid { Barcode = "BC-DST", SkidBarcode = one, StorageLocation = Store }, Gate));
        Ok(h.Yard(new ReceiveSkid { Barcode = "BC-DST", SkidBarcode = Label("PO00000011", "RTDST", "001"), StorageLocation = Store }, Gate));
    }

    [Fact]
    public async Task Received_skid_does_not_transfer_and_partial_transfer_keeps_the_source_loaded()
    {
        var h = await Fg.StackAsync();
        AtDock(h, "BC-P", "T-P", "RTP", "PO00000020", Dock1);
        AtDock(h, "BC-Q", "T-Q", "RTQ", "PO00000021", Dock2, Planned.AddHours(1));
        var one = Label("PO00000020", "RTP", "001");
        var two = Label("PO00000020", "RTP", "002");
        Ok(h.Yard(new ReceiveSkid { Barcode = "BC-P", SkidBarcode = one, StorageLocation = Store }, Gate));

        Fg.Error(h.Yard(new TransferSkids { SourceBarcode = "BC-P", DestinationBarcode = "BC-Q", ReasonNId = "Other", Comments = "x", SkidBarcodes = [one] }, Gate)).ShouldBe(NvErrors.SkidNotOnSource);
        h.Read<SkidTransload>().ShouldBeEmpty();

        Ok(h.Yard(new TransferSkids { SourceBarcode = "BC-P", DestinationBarcode = "BC-Q", ReasonNId = "Other", Comments = "x", SkidBarcodes = [two] }, Gate));
        var source = Get(h, "BC-P");
        (source.Status, source.ContentType).ShouldBe((TrailerStates.Empty, null));
        h.Read<InboundSkid>(q => q.Where(s => s.Verified)).Single().SkidNo.ShouldBe("001");
    }

    [Fact]
    public async Task Transfer_into_an_empty_trailer_makes_it_full_with_a_new_placard()
    {
        var h = await Fg.StackAsync();
        AtDock(h, "BC-A", "T-A", "RTA", "PO00000030", Dock1);
        NewTrailer(h, "BC-EMPTY", "T-EMPTY");
        Ok(h.Yard(new ArriveEmptyTrailer { Barcode = "BC-EMPTY", LocationNId = Yard2 }, Gate));
        Ok(h.Yard(new CallEmptyTrailer { Barcode = "BC-EMPTY", DockNId = Dock2 }, Gate));
        Pull(h, "BC-EMPTY", Dock2);
        var emptyPlacard = Get(h, "BC-EMPTY").PlacardNId!;

        Ok(h.Yard(new TransferSkids { SourceBarcode = "BC-A", DestinationBarcode = "BC-EMPTY", ReasonNId = "Damaged", Comments = "xe hong", SkidBarcodes = [Label("PO00000030", "RTA", "001"), Label("PO00000030", "RTA", "002")] }, Gate));

        var destination = Get(h, "BC-EMPTY");
        (destination.Status, destination.ContentType, destination.Route).ShouldBe((TrailerStates.Full, TrailerContents.Component, "RTA"));
        destination.PlacardNId!.ShouldStartWith("PL-C");
        destination.PlacardNId.ShouldNotBe(emptyPlacard);
        h.Read<TrailerPlacard>(q => q.Where(p => p.NId == emptyPlacard)).Single().TrailerNId.ShouldBeNull();
    }

    /// <summary>L19: nhãn thiếu một ký tự báo sai độ dài; đơn có số PO dài hơn ô thì nhãn đủ độ dài vẫn không khớp, câu lỗi nói skid chưa được báo.</summary>
    [Fact]
    public async Task Lab_L19_label_off_by_one_and_po_longer_than_its_cell()
    {
        var h = await Fg.StackAsync();
        AtDock(h, "BC-L", "T-L", "RTL", "PO00000040", Dock1);
        var good = Label("PO00000040", "RTL", "001");

        var shorter = h.Yard(new ReceiveSkid { Barcode = "BC-L", SkidBarcode = good[..39], StorageLocation = Store }, Gate);
        Fg.Error(shorter).ShouldBe(NvErrors.InvalidSkidLabel);
        Fg.Message(shorter).ShouldContain("got 39");

        // Dư một ký tự ở đầu: đủ cắt lệch mọi ô phía sau nếu không kiểm độ dài.
        Fg.Error(h.Yard(new ReceiveSkid { Barcode = "BC-L", SkidBarcode = " " + good, StorageLocation = Store }, Gate)).ShouldBe(NvErrors.InvalidSkidLabel);

        // Đơn PO 11 ký tự: nhãn cắt còn 10, đúng độ dài nhưng không khớp đơn.
        var nid = Schedule(h, "RTLONG", "T-LONG", "PO000000411", on: Planned.AddHours(2));
        NewTrailer(h, "BC-LONG", "T-LONG");
        Ok(h.Yard(new ArriveComponentTrailer { ScheduleNId = nid, Barcode = "BC-LONG", LocationNId = FreeYard(h) }, Gate));
        Pull(h, "BC-LONG", Dock2);
        var cut = Label("PO000000411", "RTLONG", "001");
        cut.Length.ShouldBe(SkidLabel.Length);
        var result = h.Yard(new ReceiveSkid { Barcode = "BC-LONG", SkidBarcode = cut, StorageLocation = Store }, Gate);
        Fg.Error(result).ShouldBe(NvErrors.SkidNotDeclared);
        Fg.Message(result).ShouldContain("has not declared");
        h.Read<InboundSkid>(q => q.Where(s => s.ScheduleNId == nid)).ShouldAllBe(s => !s.Verified);

        // Đối chứng dương: nhãn đúng của đơn PO 10 ký tự cùng cách dựng thì nhận được.
        Ok(h.Yard(new ReceiveSkid { Barcode = "BC-L", SkidBarcode = good, StorageLocation = Store }, Gate));
    }

    [Fact]
    public async Task Logical_dock_of_the_door_must_match_the_skid_and_a_skid_on_another_trailer_says_so()
    {
        var h = await Fg.StackAsync();
        AtDock(h, "BC-W", "T-W", "RTW", "PO00000050", DockOtherLogical);
        var label = Label("PO00000050", "RTW", "001");
        Fg.Error(h.Yard(new ReceiveSkid { Barcode = "BC-W", SkidBarcode = label, StorageLocation = Store }, Gate)).ShouldBe(NvErrors.WrongLogicalDock);

        AtDock(h, "BC-X", "T-X", "RTX", "PO00000051", Dock1, Planned.AddHours(1));
        var other = h.Yard(new ReceiveSkid { Barcode = "BC-X", SkidBarcode = label, StorageLocation = Store }, Gate);
        Fg.Error(other).ShouldBe(NvErrors.SkidNotDeclared);
        Fg.Message(other).ShouldContain("BC-W");
    }

    [Fact]
    public async Task Local_supplier_receives_listed_material_on_the_delivery_and_stays_off_the_gate_list()
    {
        var h = await Fg.StackAsync();
        var auto = Schedule(h, "LOC1", null, "PO00000060", "SUP-140", "NV-RM-CASE-B", "KB-CASE-B");
        (Sched(h, auto).GateStatus, Sched(h, auto).LoadingStatus).ShouldBe((GateStates.Completed, LoadingStates.Unloaded));
        var received = h.Read<InboundSkid>(q => q.Where(s => s.ScheduleNId == auto)).ToList();
        received.ShouldAllBe(s => s.Verified);
        h.Read<Mtu>(q => q.Where(m => m.NId == received[0].MtuNId)).Single().EquipmentNId.ShouldBe("NV1-CMN-RCV-R02");

        // Material ngoài danh sách của nhà cung cấp tại chỗ: không tự nhận, không đăng ký ở cổng, đóng tay bị chặn khi còn skid.
        var manual = Schedule(h, "LOC2", "T-LOC", "PO00000061", "SUP-140", Part, Kanban, Planned.AddHours(1));
        h.Read<InboundSkid>(q => q.Where(s => s.ScheduleNId == manual)).ShouldAllBe(s => !s.Verified);
        NewTrailer(h, "BC-LOC", "T-LOC");
        Fg.Error(h.Yard(new ArriveComponentTrailer { ScheduleNId = manual, Barcode = "BC-LOC", LocationNId = Yard1 }, Gate)).ShouldBe(NvErrors.LocalSupplierRoute);
        Fg.Error(h.Yard(new CompleteLocalSchedule { ScheduleNId = manual }, Gate)).ShouldBe(NvErrors.SkidsPending);
    }

    [Fact]
    public async Task Duplicate_delivery_and_a_changed_drop_off_behave()
    {
        var h = await Fg.StackAsync();
        Schedule(h, "RTD", "T-D");
        var again = JsonSerializer.Serialize(new { route = "RTD", plannedOn = Planned, skids = new[] { new { poNo = "P", supplier = "S", dock = "L1", pallet = "F", skid = "001", material = Part, kanban = Kanban, quantityPerBox = 1m, boxCount = 1m } } });
        Fg.Error(h.Yard(new ReceiveDeliverySchedule { Payload = again })).ShouldBe(NvErrors.InvalidDelivery);
        Fg.Error(h.Yard(new ReceiveDeliverySchedule { Payload = "{" })).ShouldBe(NvErrors.InvalidDelivery);

        NewTrailer(h, "BC-M", "T-M");
        Ok(h.Yard(new ArriveEmptyTrailer { Barcode = "BC-M", LocationNId = Yard1 }, Gate));
        Ok(h.Yard(new CreateTrailerMovementRequest { Barcode = "BC-M", DestinationNId = Dock1 }, Gate));
        var request = h.Read<TrailerMovement>(q => q.Where(m => m.TrailerNId == "BC-M")).Single();
        Ok(h.Yard(new ChangeMovementDestination { MovementNId = request.NId, DestinationNId = Dock2 }, Gate));
        Pull(h, "BC-M", Dock2);
        Get(h, "BC-M").LocationNId.ShouldBe(Dock2);
        Fg.Error(h.Yard(new ChangeMovementDestination { MovementNId = request.NId, DestinationNId = Dock1 }, Gate)).ShouldBe(NvErrors.InvalidMovement);
    }

    [Fact]
    public async Task Unplanned_locations_and_shunter_zones_filter_as_declared()
    {
        var h = await Fg.StackAsync();
        NewTrailer(h, "BC-Z", "T-Z");
        Ok(h.Yard(new ArriveEmptyTrailer { Barcode = "BC-Z", LocationNId = Yard1 }, Gate));
        Ok(h.Yard(new CreateTrailerMovementRequest { Barcode = "BC-Z", DestinationNId = Dock1 }, Gate));
        Ok(h.Yard(new AssignShuntZone { UserNId = "shunt.a", AreaNId = "NV1-CMN-RCV" }));
        Ok(h.Yard(new AssignShuntZone { UserNId = "shunt.b", AreaNId = "NV1-CMN-DOCK" }));

        using var readerSession = h.Executor.OpenRead();
        var reader = new Reader(readerSession, SolutionHarness.Caller("shunt.a", "yard"), h.Time);
        new MyTrailerMovements().Read(new NoYardParameters(), reader).ShouldBeEmpty();
        using var otherSession = h.Executor.OpenRead();
        var other = new Reader(otherSession, SolutionHarness.Caller("shunt.b", "yard"), h.Time);
        new MyTrailerMovements().Read(new NoYardParameters(), other).Count().ShouldBe(1);
        using var freeSession = h.Executor.OpenRead();
        var free = new Reader(freeSession, SolutionHarness.Caller("nobody", "yard"), h.Time);
        new MyTrailerMovements().Read(new NoYardParameters(), free).Count().ShouldBe(1);

        var doors = new UnplannedLocations().Read(new UnplannedParameters { TrailerType = UnplannedTypes.DirectMaterial }, reader).Select(e => e.NId).ToList();
        doors.ShouldBe([DirectDock]);
        Ok(h.Yard(new RegisterUnplannedTrailer { TrailerType = UnplannedTypes.DirectMaterial, Trailer = "PL-Z", Carrier = "C", LocationNId = DirectDock }, Gate));
        new UnplannedLocations().Read(new UnplannedParameters { TrailerType = UnplannedTypes.DirectMaterial }, reader).ShouldBeEmpty();
    }

    private sealed class Reader(ReadSession session, CallerIdentity caller, TimeProvider time) : IReadingPlatform
    {
        public CallerIdentity Caller => caller;

        public TimeProvider Time => time;

        public IQueryable<T> ProjectionQuery<T>()
            where T : class => session.SetOf<T>();
    }
}
