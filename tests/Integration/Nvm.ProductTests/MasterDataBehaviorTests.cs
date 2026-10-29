using Nvm.Product;

namespace Nvm.ProductTests;

/// <summary>Equipment, material, process: dữ liệu master của lớp Product.</summary>
public sealed class MasterDataBehaviorTests
{
    [Fact]
    public async Task Equipment_child_must_sit_deeper_than_its_parent()
    {
        var p = await ProductHarness.CreateAsync();

        p.Fails(new CreateEquipment { NId = "X", Level = "Plant", ParentNId = ProductHarness.Area }, ProductErrors.InvalidEquipmentLevel);
        p.Fails(new CreateEquipment { NId = "X", Level = "Floor", ParentNId = ProductHarness.Area }, ProductErrors.InvalidEquipmentLevel);
        p.Fails(new CreateEquipment { NId = "X", Level = "WorkUnit", ParentNId = "missing" }, ProductErrors.UnknownEquipment);
        p.Ok(new CreateEquipment { NId = "NV1-B1-L1-MOD-POST", Level = "Subarea", ParentNId = ProductHarness.Area });
        p.Read<Equipment>(q => q.Where(e => e.ParentNId == ProductHarness.Area)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Equipment_property_and_group_are_upserted_by_key()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new SetEquipmentProperty { EquipmentNId = ProductHarness.Dock, Name = "DockCode", Value = "D1" });
        p.Ok(new SetEquipmentProperty { EquipmentNId = ProductHarness.Dock, Name = "DockCode", Value = "D2" });
        p.Ok(new AddEquipmentToGroup { GroupNId = "LogicalDock-A", GroupType = "LogicalDock", EquipmentNId = ProductHarness.Dock });
        p.Ok(new AddEquipmentToGroup { GroupNId = "LogicalDock-A", GroupType = "LogicalDock", EquipmentNId = ProductHarness.Dock });

        p.Read<EquipmentProperty>().ShouldHaveSingleItem().Value.ShouldBe("D2");
        p.Read<EquipmentGroupMember>().ShouldHaveSingleItem().GroupType.ShouldBe("LogicalDock");
        p.Fails(new SetEquipmentProperty { EquipmentNId = "missing", Name = "x" }, ProductErrors.UnknownEquipment);
    }

    [Fact]
    public async Task Material_property_is_upserted_by_key_and_kept_as_text()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new SetMaterialProperty { MaterialNId = ProductHarness.Module, Name = "PalletQty", Value = "16" });
        p.Ok(new SetMaterialProperty { MaterialNId = ProductHarness.Module, Name = "PalletQty", Value = "8" });

        var property = p.Read<MaterialProperty>().ShouldHaveSingleItem();
        (property.NId, property.Value).ShouldBe((ProductHarness.Module + "|PalletQty", "8"));
        p.Fails(new SetMaterialProperty { MaterialNId = "missing", Name = "x" }, ProductErrors.UnknownMaterial);
    }

    [Fact]
    public async Task Only_one_material_revision_is_current()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new CreateMaterialRevision { MaterialNId = ProductHarness.Module, Revision = "V6", IsCurrent = true });
        p.Ok(new CreateMaterialRevision { MaterialNId = ProductHarness.Module, Revision = "V7", IsCurrent = true });

        p.Read<MaterialRevision>(q => q.Where(r => r.IsCurrent)).ShouldHaveSingleItem().Revision.ShouldBe("V7");
        p.Fails(new CreateMaterial { NId = ProductHarness.Module, UnitOfMeasure = "pc" }, ProductErrors.DuplicateNId);

        p.Ok(new UpdateMaterial { NId = ProductHarness.Module, MaterialClass = "Finished" });
        var module = p.Read<Material>(q => q.Where(m => m.NId == ProductHarness.Module)).Single();
        (module.MaterialClass, module.Name).ShouldBe(("Finished", ProductHarness.Module));
        p.Fails(new UpdateMaterial { NId = "missing" }, ProductErrors.UnknownMaterial);
    }

    [Fact]
    public async Task Process_defaults_its_bop_to_its_own_name_and_orders_operations_by_sequence()
    {
        var p = await ProductHarness.CreateAsync();

        var process = p.Read<Process>().ShouldHaveSingleItem();
        process.BopNId.ShouldBe(ProductHarness.Process);
        p.Read<ProcessOperation>(q => q.OrderBy(o => o.Sequence)).Select(o => o.OperationNId).ShouldBe(["OP10", "OP20"]);
        p.Read<OperationMaterial>().ShouldHaveSingleItem().Usage.ShouldBe("NormalPart");
        p.Fails(new CreateProcess { NId = "P2", ProductMaterialNId = "missing" }, ProductErrors.UnknownMaterial);
        p.Fails(new AllowOperationEquipment { ProcessOperationNId = ProductHarness.Process + ":OP20", EquipmentNId = "missing" },
            ProductErrors.UnknownEquipment);
    }
}
