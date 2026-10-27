using Nvm.Product;

namespace Nvm.ProductTests;

/// <summary>Buffer (mỗi lô một buffer, whitelist) và yêu cầu vận chuyển.</summary>
public sealed class LogisticsBehaviorTests
{
    [Fact]
    public async Task A_lot_belongs_to_one_buffer_and_must_be_detached_before_moving_to_another()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateBuffer { NId = "1D07-4D-01", EquipmentNId = ProductHarness.Dock });
        p.Ok(new CreateBuffer { NId = ProductHarness.Subarea + "_Buffer", EquipmentNId = ProductHarness.Subarea });
        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 1 });

        p.Ok(new AttachMtuToBuffer { BufferNId = "1D07-4D-01", MtuNId = "LOT-1" });
        p.Fails(new AttachMtuToBuffer { BufferNId = ProductHarness.Subarea + "_Buffer", MtuNId = "LOT-1" }, ProductErrors.AlreadyInBuffer);

        p.Ok(new DetachMtuFromBuffer { MtuNId = "LOT-1" });
        p.Ok(new AttachMtuToBuffer { BufferNId = ProductHarness.Subarea + "_Buffer", MtuNId = "LOT-1" });
        p.Read<BufferAssignment>().ShouldHaveSingleItem().BufferNId.ShouldBe(ProductHarness.Subarea + "_Buffer");
        p.Fails(new DetachMtuFromBuffer { MtuNId = "LOT-404" }, ProductErrors.NotInBuffer);
    }

    [Fact]
    public async Task Buffer_with_a_whitelist_refuses_other_materials()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateBuffer { NId = "B-1", EquipmentNId = ProductHarness.Subarea });
        p.Ok(new AddBufferWhitelist { BufferNId = "B-1", MaterialNId = ProductHarness.Paste });
        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 1 });
        p.Ok(new CreateMtu { NId = "M-1", MaterialNId = ProductHarness.Module });

        p.Ok(new AttachMtuToBuffer { BufferNId = "B-1", MtuNId = "LOT-1" });
        p.Fails(new AttachMtuToBuffer { BufferNId = "B-1", MtuNId = "M-1" }, ProductErrors.NotWhitelisted);
    }

    [Fact]
    public async Task Logistic_request_moves_the_mtu_through_the_flow_to_its_target()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 1, EquipmentNId = ProductHarness.Dock });
        p.Ok(new CreateLogisticRequest { NId = "LR-1", MtuNId = "LOT-1", ToEquipmentNId = ProductHarness.Subarea });

        p.Fails(new MoveLogisticRequestWithinFlow { NId = "LR-1", EquipmentNId = ProductHarness.Area }, ProductErrors.InvalidLogisticStatus);
        p.Ok(new ReleaseLogisticRequest { NId = "LR-1" });
        p.Ok(new MoveLogisticRequestWithinFlow { NId = "LR-1", EquipmentNId = ProductHarness.Area });
        p.Mtu("LOT-1").EquipmentNId.ShouldBe(ProductHarness.Area);

        p.Ok(new CompleteLogisticRequest { NId = "LR-1" });
        p.Mtu("LOT-1").EquipmentNId.ShouldBe(ProductHarness.Subarea);
        var request = p.Read<LogisticRequest>().Single();
        request.Status.ShouldBe(LogisticStatus.Completed);
        request.FromEquipmentNId.ShouldBe(ProductHarness.Dock);
        p.Fails(new CompleteLogisticRequest { NId = "LR-1" }, ProductErrors.InvalidLogisticStatus);
    }
}
