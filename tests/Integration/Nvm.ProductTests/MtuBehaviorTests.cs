using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.ProductTests;

/// <summary>MTU, vật chứa và lịch sử MTU.</summary>
public sealed class MtuBehaviorTests
{
    [Fact]
    public async Task Serialized_material_gives_quantity_one_and_batch_keeps_its_quantity()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new CreateMtu { NId = "M-1", MaterialNId = ProductHarness.Module });
        p.Fails(new CreateMtu { NId = "M-2", MaterialNId = ProductHarness.Module, Quantity = 2 }, ProductErrors.SerialQuantity);
        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 1.5m, EquipmentNId = ProductHarness.Subarea });

        var serial = p.Mtu("M-1");
        serial.Kind.ShouldBe("Serial");
        serial.Quantity.ShouldBe(1m);
        serial.Status.ShouldBe(MtuStatus.Available);
        serial.EquipmentNId.ShouldBe(EquipmentLevels.NotApplicable);
        p.Mtu("LOT-1").Quantity.ShouldBe(1.5m);
        p.Fails(new EditMtu { NId = "M-1", Quantity = 3 }, ProductErrors.SerialQuantity);
    }

    [Fact]
    public async Task History_records_create_assemble_and_edit_in_order()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 2 });
        p.Ok(new CreateMtua { NId = "TOTE-1", ContainerType = "Tote", EquipmentNId = ProductHarness.Subarea });
        p.Ok(new AssembleIntoMtua { ChildNId = "LOT-1", ContainerNId = "TOTE-1" });
        p.Ok(new EditMtu { NId = "LOT-1", Quantity = 1.75m });

        var rows = p.Read<MtuHistory>(q => q.Where(h => h.MtuNId == "LOT-1").OrderBy(h => h.CreatedOn));
        rows.Select(h => h.Action).ShouldBe([MtuHistoryActions.Create, MtuHistoryActions.Assemble, MtuHistoryActions.Edit]);
        rows[1].ContainerNId.ShouldBe("TOTE-1");
        rows[2].Detail.ShouldBe("Quantity 2 -> 1.75");
    }

    [Fact]
    public async Task Location_inside_a_container_is_the_equipment_of_the_outermost_container()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new CreateMtu { NId = "KIT-1", MaterialNId = ProductHarness.Module, EquipmentNId = ProductHarness.Machine });
        p.Ok(new CreateMtua { NId = "TOTE-1", ContainerType = "Tote" });
        p.Ok(new CreateMtua { NId = "PAL-1", ContainerType = "Pallet", EquipmentNId = ProductHarness.Dock });
        p.Ok(new AssembleIntoMtua { ChildNId = "KIT-1", ContainerNId = "TOTE-1" });
        p.Ok(new AssembleIntoMtua { ChildNId = "TOTE-1", ContainerNId = "PAL-1" });

        p.Mtu("KIT-1").EquipmentNId.ShouldBeNull();
        MtuLocations.EquipmentOf(p.Read<Mtu>().AsQueryable(), p.Read<Mtua>().AsQueryable(), "KIT-1").ShouldBe(ProductHarness.Dock);
        p.Fails(new AssembleIntoMtua { ChildNId = "PAL-1", ContainerNId = "TOTE-1" }, ProductErrors.ContainerCycle);
        p.Fails(new MoveMtu { NId = "KIT-1", EquipmentNId = ProductHarness.Machine }, ProductErrors.InsideContainer);
    }

    [Fact]
    public async Task Assembling_an_mtu_straight_onto_a_pallet_with_totes_only_attaches_it()
    {
        // Tháo hết tote khỏi pallet là post-action của phía dự án (R7); lớp Product chỉ gắn.
        var p = await ProductHarness.CreateAsync();

        p.Ok(new CreateMtua { NId = "PAL-1", ContainerType = "Pallet", EquipmentNId = ProductHarness.Dock });
        p.Ok(new CreateMtua { NId = "TOTE-1", ContainerType = "Tote" });
        p.Ok(new AssembleIntoMtua { ChildNId = "TOTE-1", ContainerNId = "PAL-1" });
        p.Ok(new CreateMtu { NId = "KIT-9", MaterialNId = ProductHarness.Module });
        p.Ok(new AssembleIntoMtua { ChildNId = "KIT-9", ContainerNId = "PAL-1" });

        p.Read<Mtua>(q => q.Where(c => c.ParentMtuaNId == "PAL-1")).ShouldHaveSingleItem().NId.ShouldBe("TOTE-1");
        p.Read<Mtu>(q => q.Where(m => m.ParentMtuaNId == "PAL-1")).ShouldHaveSingleItem().NId.ShouldBe("KIT-9");
    }

    [Fact]
    public async Task Removing_from_a_container_puts_the_item_on_equipment_and_move_records_an_edit()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new CreateMtua { NId = "TOTE-1", ContainerType = "Tote" });
        p.Ok(new CreateMtu { NId = "KIT-1", MaterialNId = ProductHarness.Module });
        p.Ok(new AssembleIntoMtua { ChildNId = "KIT-1", ContainerNId = "TOTE-1" });
        p.Ok(new RemoveFromMtua { ChildNId = "KIT-1", EquipmentNId = ProductHarness.Subarea });
        p.Ok(new MoveMtu { NId = "KIT-1", EquipmentNId = ProductHarness.Machine });

        var kit = p.Mtu("KIT-1");
        kit.ParentMtuaNId.ShouldBeNull();
        kit.EquipmentNId.ShouldBe(ProductHarness.Machine);
        p.Read<MtuHistory>(q => q.Where(h => h.MtuNId == "KIT-1" && h.Action == MtuHistoryActions.Edit)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Split_creates_child_dot_n_with_the_split_quantity()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 1m, EquipmentNId = ProductHarness.Subarea });
        p.Ok(new SplitMtu { NId = "LOT-1", Quantity = 0.06m }).NId.ShouldBe("LOT-1.1");
        p.Ok(new SplitMtu { NId = "LOT-1", Quantity = 0.04m }).NId.ShouldBe("LOT-1.2");

        p.Mtu("LOT-1").Quantity.ShouldBe(0.9m);
        var child = p.Mtu("LOT-1.1");
        child.Quantity.ShouldBe(0.06m);
        child.EquipmentNId.ShouldBe(ProductHarness.Subarea);
        p.Fails(new SplitMtu { NId = "LOT-1", Quantity = 0.91m }, ProductErrors.InvalidQuantity);
        p.Ok(new SplitMtu { NId = "LOT-1", Quantity = 0.9m }).NId.ShouldBe("LOT-1.3");
        p.Mtu("LOT-1").Quantity.ShouldBe(0m);
    }

    [Fact]
    public async Task Container_status_follows_its_data_driven_machine_and_forcing_needs_a_defined_status()
    {
        // State machine của vật chứa là dữ liệu của người dùng sản phẩm, như của WO.
        var p = await ProductHarness.CreateAsync();
        foreach (var status in new[] { "Empty", "Full" })
        {
            p.Executor.Execute("Foundation", new DefineStatus { Machine = "LabPallet", Status = status });
        }

        p.Executor.Execute("Foundation", new DefineTransition { Machine = "LabPallet", FromStatus = "", ToStatus = "Empty" });
        p.Executor.Execute("Foundation", new DefineTransition { Machine = "LabPallet", FromStatus = "Empty", ToStatus = "Full" });

        p.Ok(new CreateMtua { NId = "PAL-S", ContainerType = "Pallet", StateMachine = "LabPallet", Status = "Empty" });
        p.Fails(new CreateMtua { NId = "PAL-T", ContainerType = "Pallet", StateMachine = "LabPallet" }, ProductErrors.InvalidContainerStatus);
        p.Fails(new CreateMtua { NId = "PAL-U", ContainerType = "Pallet", StateMachine = "LabPallet", Status = "Full" }, 7101);
        p.Ok(new SetMtuaStatus { NId = "PAL-S", Status = "Full" });
        p.Fails(new SetMtuaStatus { NId = "PAL-S", Status = "Empty" }, 7101);
        p.Ok(new SetMtuaStatusForced { NId = "PAL-S", Status = "Empty" });
        p.Fails(new SetMtuaStatusForced { NId = "PAL-S", Status = "Staged" }, ProductErrors.InvalidContainerStatus);

        p.Read<Mtua>(q => q.Where(c => c.NId == "PAL-S")).Single().Status.ShouldBe("Empty");
        p.Ok(new CreateMtua { NId = "BOX-1", ContainerType = "Box" });
        p.Fails(new SetMtuaStatus { NId = "BOX-1", Status = "Full" }, ProductErrors.InvalidContainerStatus);
    }

    [Fact]
    public async Task Container_quantity_and_material_are_set_by_the_caller_and_never_counted_by_the_product_layer()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateMtua { NId = "PAL-Q", ContainerType = "Pallet" });
        p.Ok(new CreateMtua { NId = "TOTE-Q", ContainerType = "Tote" });
        p.Ok(new AssembleIntoMtua { ChildNId = "TOTE-Q", ContainerNId = "PAL-Q" });

        p.Read<Mtua>(q => q.Where(c => c.NId == "PAL-Q")).Single().Quantity.ShouldBe(0m);
        p.Ok(new UpdateMtua { NId = "PAL-Q", Quantity = 1, MaterialNId = ProductHarness.Module });
        p.Fails(new UpdateMtua { NId = "PAL-Q", Quantity = -1 }, ProductErrors.InvalidQuantity);
        p.Fails(new UpdateMtua { NId = "PAL-Q", MaterialNId = "missing" }, ProductErrors.UnknownMaterial);
        var pallet = p.Read<Mtua>(q => q.Where(c => c.NId == "PAL-Q")).Single();
        (pallet.Quantity, pallet.MaterialNId).ShouldBe((1m, ProductHarness.Module));

        p.Ok(new UpdateMtua { NId = "PAL-Q", ClearMaterial = true });
        p.Read<Mtua>(q => q.Where(c => c.NId == "PAL-Q")).Single().MaterialNId.ShouldBeNull();
    }

    [Fact]
    public async Task Moving_a_container_takes_it_out_of_its_parent_and_deleting_needs_it_empty_then_the_code_is_free_again()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateMtua { NId = "TRL-M", ContainerType = "StorageTrailer" });
        p.Ok(new CreateMtua { NId = "PAL-M", ContainerType = "Pallet" });
        p.Ok(new AssembleIntoMtua { ChildNId = "PAL-M", ContainerNId = "TRL-M" });

        p.Fails(new DeleteMtua { NId = "TRL-M" }, ProductErrors.ContainerNotEmpty);
        p.Fails(new MoveMtua { NId = "PAL-M", EquipmentNId = "missing" }, ProductErrors.UnknownEquipment);
        p.Ok(new MoveMtua { NId = "PAL-M", EquipmentNId = ProductHarness.Dock });

        var pallet = p.Read<Mtua>(q => q.Where(c => c.NId == "PAL-M")).Single();
        (pallet.ParentMtuaNId, pallet.EquipmentNId).ShouldBe((null, ProductHarness.Dock));
        p.Ok(new DeleteMtua { NId = "TRL-M" });
        p.Read<Mtua>(q => q.Where(c => c.NId == "TRL-M")).ShouldBeEmpty();
        p.Ok(new CreateMtua { NId = "TRL-M", ContainerType = "StorageTrailer" });
        p.Fails(new MoveMtua { NId = "PAL-NONE", EquipmentNId = ProductHarness.Dock }, ProductErrors.UnknownContainer);
    }
}
