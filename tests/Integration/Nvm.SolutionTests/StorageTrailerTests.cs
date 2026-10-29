using Nvm.Foundation;
using Nvm.Product;
using Nvm.Project;
using Nvm.Solution;

namespace Nvm.SolutionTests;

/// <summary>
/// R7: trailer kho. Vòng đời (đặt loại kho, gọi vào dock, nạp, Complete Load, dỡ, ra yard, gỡ loại kho), ma trận luật nạp, loại
/// kho chung khai bằng ghi chú, và lab dỡ pallet cuối bằng lệnh dỡ một pallet.
/// </summary>
public sealed class StorageTrailerTests
{
    private const string Dock = "NV1-CMN-DOCK-D01";
    private const string Yard1 = "NV1-CMN-YARD-Y01";
    private const string Yard2 = "NV1-CMN-YARD-Y02";

    private static readonly CallerIdentity YardUser = SolutionHarness.Caller("yard.nv1", "yard");
    private static readonly CallerIdentity Shipper = SolutionHarness.Caller("ship.nv1", "shipping");

    [Fact]
    public async Task Finished_goods_storage_trailer_goes_from_empty_to_tapped_to_full_then_unloads_back_and_unsets_only_away_from_the_dock()
    {
        var h = await Fg.StackAsync();
        Trailer(h, "TRL-1", StorageTypes.FinishedGoods);
        Fg.Error(h.Yard(new SetTrailerAsStorage { Barcode = "TRL-1", ContentType = StorageTypes.Generic }, YardUser)).ShouldBe(NvErrors.InvalidTrailerState);
        CallToDock(h, "TRL-1", Dock);
        Schedule(h, "TRL-1").Status.ShouldBe(ScheduleStates.ToBeLoaded);

        Fg.Pallet(h, "TL-P1", "NV-FG-MOD-B60", totes: 6);
        Fg.Pallet(h, "TL-P2", "NV-FG-MOD-B60", totes: 6);
        Ok(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRL-1", PalletNId = "TL-P1" }, Shipper));
        Get(h, "TRL-1").Status.ShouldBe(TrailerStates.Tapped);
        Schedule(h, "TRL-1").Status.ShouldBe(ScheduleStates.Tapped);
        Ok(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRL-1", PalletNId = "TL-P2" }, Shipper));
        Fg.Detail(h, "TL-P2").LoadedOn.ShouldBe(h.Time.GetUtcNow());

        Ok(h.Yard(new CompleteStorageTrailerLoad { Barcode = "TRL-1" }, Shipper));
        h.DeliverEvents();
        Get(h, "TRL-1").Status.ShouldBe(TrailerStates.Full);
        Schedule(h, "TRL-1").Status.ShouldBe(ScheduleStates.ToCallForShipping);
        var box = Fg.Container(h, "TRL-1");
        (box.Status, box.Quantity, box.MaterialNId).ShouldBe((PalletStates.Full, 2m, "NV-FG-MOD-B60"));
        Fg.Container(h, "TL-P1").Status.ShouldBe(PalletStates.Full);
        h.Read<FifoPallet>().Select(f => (f.NId, f.LocationNId)).Order().ShouldBe([("TL-P1", Dock), ("TL-P2", Dock)]);
        Fg.Error(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRL-1", PalletNId = "TL-P1" }, Shipper)).ShouldBe(NvErrors.InvalidTrailerState);

        // Dỡ cả trailer: pallet về cửa dock, trailer rỗng nhưng giữ loại kho; gỡ loại kho phải ra khỏi cửa dock trước.
        Ok(h.Yard(new UnloadStorageTrailer { Barcode = "TRL-1" }, Shipper));
        var unloaded = Get(h, "TRL-1");
        (unloaded.Status, unloaded.ContentType, unloaded.LocationNId).ShouldBe((TrailerStates.Empty, StorageTypes.FinishedGoods, Dock));
        (Fg.Container(h, "TRL-1").Status, Fg.Container(h, "TRL-1").Quantity).ShouldBe((PalletStates.Empty, 0m));
        Schedule(h, "TRL-1", open: false).Status.ShouldBe(ScheduleStates.Unloaded);
        var pallet = Fg.Container(h, "TL-P1");
        (pallet.ParentMtuaNId, pallet.EquipmentNId, pallet.Status).ShouldBe((null, Dock, PalletStates.Full));
        Fg.Error(h.Yard(new UnsetTrailerAsStorage { Barcode = "TRL-1" }, YardUser)).ShouldBe(NvErrors.TrailerAtDock);

        Move(h, "TRL-1", Yard2);
        Ok(h.Yard(new UnsetTrailerAsStorage { Barcode = "TRL-1" }, YardUser));
        var plain = Get(h, "TRL-1");
        (plain.ContentType, plain.StorageMtuaNId, plain.LocationNId).ShouldBe((null, null, Yard2));
        h.Read<Mtua>(q => q.Where(c => c.NId == "TRL-1")).ShouldBeEmpty();
        h.Read<TrailerSchedule>(q => q.Where(s => s.TrailerNId == "TRL-1")).ShouldAllBe(s => s.Status == ScheduleStates.Closed);

        // Đặt lại loại kho được (MTUA cũ đã xóa mềm, tạo lại cùng mã).
        Ok(h.Yard(new SetTrailerAsStorage { Barcode = "TRL-1", ContentType = StorageTypes.Generic }, YardUser));
    }

    /// <summary>Ma trận luật nạp: mỗi dòng một pallet quét vào trailer đã có (hoặc chưa có) pallet đầu.</summary>
    [Theory]
    [InlineData("first", null, 0)]
    [InlineData("same material", "same", 0)]
    [InlineData("other material", "other", NvErrors.MaterialMismatch)]
    [InlineData("pallet not full", "empty", NvErrors.InvalidPallet)]
    [InlineData("held item", "held", NvErrors.HeldOrScrapped)]
    [InlineData("held item, check off by key", "held-key-off", 0)]
    [InlineData("tested after untested", "tested", NvErrors.MixedTestState)]
    [InlineData("other material and tested: material first", "other-tested", NvErrors.MaterialMismatch)]
    [InlineData("pallet already in another trailer", "elsewhere", NvErrors.InvalidPallet)]
    public async Task Load_rules_hold_in_order(string label, string? second, int expected)
    {
        var h = await Fg.StackAsync();
        Trailer(h, "TRM", StorageTypes.FinishedGoods);
        CallToDock(h, "TRM", Dock);
        Fg.Pallet(h, "RM-P1", "NV-FG-KIT-A", totes: 8);

        if (second is null)
        {
            Ok(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRM", PalletNId = "RM-P1" }, Shipper));
            Get(h, "TRM").Status.ShouldBe(TrailerStates.Tapped, label);
            return;
        }

        Ok(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRM", PalletNId = "RM-P1" }, Shipper));
        var pallet = second switch
        {
            "same" => Fg.Pallet(h, "RM-P2", "NV-FG-KIT-A", totes: 8),
            "other" => Fg.Pallet(h, "RM-P2", "NV-FG-MOD-B60", totes: 6),
            "empty" => Fg.Pallet(h, "RM-P2", "NV-FG-KIT-A", totes: 8),
            "held" or "held-key-off" => Fg.Pallet(h, "RM-P2", "NV-FG-KIT-A", totes: 8),
            "tested" => Fg.TestedPallet(h, "RM-P2"),
            "other-tested" => Fg.Pallet(h, "RM-P2", "NV-FG-MOD-B60", totes: 6),
            "elsewhere" => Fg.Pallet(h, "RM-P2", "NV-FG-KIT-A", totes: 8),
            _ => throw new ArgumentOutOfRangeException(nameof(second)),
        };
        switch (second)
        {
            case "empty":
                Ok(h.Run(new SetMtuaStatusForced { NId = pallet, Status = PalletStates.Empty }));
                break;
            case "held" or "held-key-off":
                Ok(h.Run(new CreateNonConformance { NId = "NC-RM-1", MtuNIds = ["NV-FG-KIT-A_RM-P2-K03"] }));
                if (second == "held-key-off")
                {
                    Ok(h.Run(new SetConfigurationKey { NId = NvKeys.StorageTrailerHeldCheck, Val = "false" }, app: SolutionManifest.FoundationAppName));
                }

                break;
            case "other-tested":
                // Pallet khác material: luật material đứng trước luật đo cuối nên chưa cần đo.
                break;
            case "elsewhere":
                Trailer(h, "TRM2", StorageTypes.FinishedGoods);
                CallToDock(h, "TRM2", "NV1-CMN-DOCK-D02");
                Ok(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRM2", PalletNId = pallet }, Shipper));
                break;
        }

        var result = h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRM", PalletNId = pallet }, Shipper);

        Fg.Error(result).ShouldBe(expected == 0 ? -1 : expected, label + ": " + Fg.Message(result));
        var inside = h.Read<Mtua>(q => q.Where(c => c.ParentMtuaNId == "TRM")).Select(c => c.NId).Order().ToList();
        inside.ShouldBe(expected == 0 ? ["RM-P1", "RM-P2"] : ["RM-P1"], label);
        if (expected != 0)
        {
            // Pallet bị từ chối giữ nguyên vị trí và trạng thái.
            Fg.Container(h, pallet).ParentMtuaNId.ShouldNotBe("TRM");
        }
    }

    [Fact]
    public async Task Storage_trailer_needs_a_dock_and_a_finished_goods_type_and_waits_for_the_test_when_pallets_are_untested()
    {
        var h = await Fg.StackAsync();
        Trailer(h, "TRT", StorageTypes.FinishedGoods);
        Fg.Pallet(h, "TT-P1", "NV-FG-KIT-A", totes: 8);
        Fg.Error(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRT", PalletNId = "TT-P1" }, Shipper)).ShouldBe(NvErrors.TrailerNotAtDock);
        Fg.Error(h.Yard(new CallStorageTrailer { Barcode = "TRT", DockNId = Yard2 }, YardUser)).ShouldBe(NvErrors.TrailerNotAtDock);

        CallToDock(h, "TRT", Dock);
        Ok(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRT", PalletNId = "TT-P1" }, Shipper));
        Ok(h.Yard(new CompleteStorageTrailerLoad { Barcode = "TRT" }, Shipper));

        // Kit chưa đo: lịch chờ gọi đi đo, không chờ giao.
        Schedule(h, "TRT").Status.ShouldBe(ScheduleStates.ToCallForTest);
    }

    [Fact]
    public async Task Generic_storage_trailer_is_declared_full_and_unloaded_with_comments_and_tracks_no_pallet()
    {
        var h = await Fg.StackAsync();
        Trailer(h, "TRG", StorageTypes.Generic);
        CallToDock(h, "TRG", Dock);
        Fg.Pallet(h, "TG-P1", "NV-FG-MOD-B60", totes: 6);
        Fg.Error(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRG", PalletNId = "TG-P1" }, Shipper)).ShouldBe(NvErrors.TrailerNotAtDock);

        Ok(h.Yard(new CompleteStorageTrailerLoad { Barcode = "TRG", Comments = "Lab generic - load" }, Shipper));
        Get(h, "TRG").Status.ShouldBe(TrailerStates.Full);
        Schedule(h, "TRG").Status.ShouldBe(ScheduleStates.Loaded);
        h.Read<Mtua>(q => q.Where(c => c.ParentMtuaNId == "TRG")).ShouldBeEmpty();

        Ok(h.Yard(new UnloadStorageTrailer { Barcode = "TRG", Comments = "Lab generic - unload" }, Shipper));
        var schedule = Schedule(h, "TRG", open: false);
        (schedule.Status, schedule.Comments).ShouldBe((ScheduleStates.Unloaded, "Lab generic - load" + Environment.NewLine + "Lab generic - unload"));
        (Get(h, "TRG").Status, Get(h, "TRG").ContentType).ShouldBe((TrailerStates.Empty, StorageTypes.Generic));
    }

    [Fact]
    public async Task Movement_requests_go_one_step_at_a_time_refuse_a_busy_destination_and_carry_the_pallets_along()
    {
        var h = await Fg.StackAsync();
        Trailer(h, "TRV", StorageTypes.FinishedGoods);
        Trailer(h, "TRW", StorageTypes.FinishedGoods, Yard2);

        Fg.Error(h.Yard(new CreateTrailerMovementRequest { Barcode = "TRV", DestinationNId = Yard2 }, YardUser)).ShouldBe(NvErrors.LocationBusy);
        Fg.Error(h.Yard(new CreateTrailerMovementRequest { Barcode = "TRV", DestinationNId = Yard1 }, YardUser)).ShouldBe(NvErrors.InvalidMovement);
        var request = h.Yard(new CreateTrailerMovementRequest { Barcode = "TRV", DestinationNId = Dock }, YardUser).As<YardResponse>().NId!;
        Fg.Error(h.Yard(new CreateTrailerMovementRequest { Barcode = "TRV", DestinationNId = "NV1-CMN-DOCK-D02" }, YardUser)).ShouldBe(NvErrors.MovementOpen);
        Fg.Error(h.Yard(new CreateTrailerMovementRequest { Barcode = "TRW", DestinationNId = Dock }, YardUser)).ShouldBe(NvErrors.LocationBusy);
        Fg.Error(h.Yard(new UpdateTrailerMovement { MovementNId = request, Status = MovementStates.Picked }, YardUser)).ShouldBe(NvErrors.InvalidMovement);

        foreach (var step in new[] { MovementStates.Accepted, MovementStates.Picked })
        {
            Ok(h.Yard(new UpdateTrailerMovement { MovementNId = request, Status = step }, YardUser));
            Get(h, "TRV").LocationNId.ShouldBe(Yard1);
        }

        Ok(h.Yard(new UpdateTrailerMovement { MovementNId = request, Status = MovementStates.Dropped }, YardUser));
        Get(h, "TRV").LocationNId.ShouldBe(Dock);
        Fg.Container(h, "TRV").EquipmentNId.ShouldBe(Dock);

        // Pallet trong trailer kho đi theo trailer: vị trí của pallet là vị trí của tầng ngoài cùng.
        Fg.Pallet(h, "TV-P1", "NV-FG-MOD-B60", totes: 6);
        Ok(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRV", PalletNId = "TV-P1" }, Shipper));
        Move(h, "TRV", "NV1-CMN-DOCK-D03");
        using var session = h.Executor.OpenRead();
        MtuLocations.EquipmentOf(session.SetOf<Mtu>(), session.SetOf<Mtua>(), "TV-P1").ShouldBe("NV1-CMN-DOCK-D03");
    }

    [Fact]
    public async Task Lab_unloading_the_last_pallet_one_by_one_leaves_the_trailer_full_with_its_old_quantity()
    {
        var h = await Fg.StackAsync();
        Trailer(h, "TRU", StorageTypes.FinishedGoods);
        CallToDock(h, "TRU", Dock);
        Fg.Pallet(h, "TU-P1", "NV-FG-MOD-B60", totes: 6);
        Ok(h.Yard(new LoadPalletIntoStorageTrailer { Barcode = "TRU", PalletNId = "TU-P1" }, Shipper));
        Ok(h.Yard(new CompleteStorageTrailerLoad { Barcode = "TRU" }, Shipper));

        Fg.Error(h.Yard(new UnloadPalletFromStorageTrailer { Barcode = "TRU", PalletNId = "TU-P1", LocationNId = "NOWHERE" }, Shipper))
            .ShouldBe(NvErrors.UnknownLocation);
        Fg.Error(h.Yard(new UnloadPalletFromStorageTrailer { Barcode = "TRU", PalletNId = "TX-NONE", LocationNId = Dock }, Shipper))
            .ShouldBe(NvErrors.NotInTrailer);
        Ok(h.Yard(new UnloadPalletFromStorageTrailer { Barcode = "TRU", PalletNId = "TU-P1", LocationNId = Dock }, Shipper));

        // Triệu chứng: pallet đã rời trailer, trailer vẫn Full, số lượng của vật chứa trailer vẫn 1.
        (Fg.Container(h, "TU-P1").ParentMtuaNId, Fg.Container(h, "TU-P1").EquipmentNId).ShouldBe((null, Dock));
        Get(h, "TRU").Status.ShouldBe(TrailerStates.Full);
        var box = Fg.Container(h, "TRU");
        (box.Status, box.Quantity).ShouldBe((PalletStates.Full, 1m));
        h.Read<Mtua>(q => q.Where(c => c.ParentMtuaNId == "TRU")).ShouldBeEmpty();

        // Cách đúng cho trailer: dỡ cả trailer (không còn pallet nào nhưng vẫn đặt trailer, vật chứa về Empty và 0).
        Ok(h.Yard(new UnloadStorageTrailer { Barcode = "TRU" }, Shipper));
        (Get(h, "TRU").Status, Fg.Container(h, "TRU").Quantity).ShouldBe((TrailerStates.Empty, 0m));
    }

    private static void Trailer(SolutionHarness h, string barcode, string type, string yard = Yard1)
    {
        Ok(h.Yard(new CreateTrailer { Barcode = barcode }, YardUser));
        Get(h, barcode).Status.ShouldBe(TrailerStates.Free);
        Ok(h.Yard(new ArriveEmptyTrailer { Barcode = barcode, LocationNId = yard }, YardUser));
        Ok(h.Yard(new SetTrailerAsStorage { Barcode = barcode, ContentType = type }, YardUser));
    }

    private static void CallToDock(SolutionHarness h, string barcode, string dock)
    {
        var request = h.Yard(new CallStorageTrailer { Barcode = barcode, DockNId = dock }, YardUser);
        Ok(request);
        Drop(h, request.As<YardResponse>().NId!);
    }

    private static void Move(SolutionHarness h, string barcode, string destination)
    {
        var request = h.Yard(new CreateTrailerMovementRequest { Barcode = barcode, DestinationNId = destination }, YardUser);
        Ok(request);
        Drop(h, request.As<YardResponse>().NId!);
    }

    private static void Drop(SolutionHarness h, string movement)
    {
        foreach (var step in new[] { MovementStates.Accepted, MovementStates.Picked, MovementStates.Dropped })
        {
            Ok(h.Yard(new UpdateTrailerMovement { MovementNId = movement, Status = step }, YardUser));
        }
    }

    private static Trailer Get(SolutionHarness h, string barcode) => h.Read<Trailer>(q => q.Where(t => t.NId == barcode)).Single();

    private static TrailerSchedule Schedule(SolutionHarness h, string barcode, bool open = true) =>
        h.Read<TrailerSchedule>(q => q.Where(s => s.TrailerNId == barcode).OrderByDescending(s => s.CreatedOn)).First(s =>
            !open || !ScheduleStates.Closing.Contains(s.Status!));

    private static void Ok(Nvm.Foundation.Runtime.CommandResult result) => result.StatusCode.ShouldBe(200, result.Body.ToJsonString());
}
