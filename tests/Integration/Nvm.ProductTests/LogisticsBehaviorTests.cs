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
    public async Task Link_ignores_the_whitelist_but_still_refuses_a_lot_in_another_buffer()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateBuffer { NId = "B-1", EquipmentNId = ProductHarness.Subarea });
        p.Ok(new CreateBuffer { NId = "B-2", EquipmentNId = ProductHarness.Dock });
        p.Ok(new AddBufferWhitelist { BufferNId = "B-1", MaterialNId = ProductHarness.Module });
        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 1 });

        p.Ok(new LinkMtuToBuffer { BufferNId = "B-1", MtuNId = "LOT-1" });
        p.Fails(new LinkMtuToBuffer { BufferNId = "B-2", MtuNId = "LOT-1" }, ProductErrors.AlreadyInBuffer);
    }

    [Fact]
    public async Task Transport_operation_moves_the_lot_between_buffers_only_when_it_completes()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateBuffer { NId = "FROM", EquipmentNId = ProductHarness.Dock });
        p.Ok(new CreateBuffer { NId = "TO", EquipmentNId = ProductHarness.Subarea });
        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 5, EquipmentNId = ProductHarness.Dock });
        p.Ok(new AttachMtuToBuffer { BufferNId = "FROM", MtuNId = "LOT-1" });

        p.Fails(new CreateLogisticRequest { NId = "LR-0", MaterialNId = ProductHarness.Paste, Quantity = 0, DestinationBufferNId = "TO" }, ProductErrors.InvalidQuantity);
        p.Fails(new CreateLogisticRequest { NId = "LR-0", MaterialNId = ProductHarness.Paste, Quantity = 1, DestinationBufferNId = "NONE" }, ProductErrors.UnknownBuffer);
        p.Ok(new CreateLogisticRequest { NId = "LR-1", MaterialNId = ProductHarness.Paste, Quantity = 5, SourceBufferNId = "FROM", DestinationBufferNId = "TO" });
        p.Fails(new AcceptMaterialRequest { LogisticRequestNId = "LR-9", TransportOperationNId = "TO-1" }, ProductErrors.UnknownLogisticRequest);
        p.Ok(new AcceptMaterialRequest { LogisticRequestNId = "LR-1", TransportOperationNId = "TO-1" });
        p.Read<TransportOperation>().Single().Status.ShouldBe(TransportStatus.Released);

        p.Ok(new StartTransportOperation { NId = "TO-1" });
        p.Fails(new CancelTransportOperation { NId = "TO-1" }, ProductErrors.InvalidLogisticStatus);
        p.Mtu("LOT-1").EquipmentNId.ShouldBe(ProductHarness.Dock);

        p.Ok(new CompleteTransportOperation { NId = "TO-1", MtuNId = "LOT-1" });
        p.Mtu("LOT-1").EquipmentNId.ShouldBe(ProductHarness.Subarea);
        p.Read<BufferAssignment>().ShouldHaveSingleItem().BufferNId.ShouldBe("TO");
        p.Fails(new CompleteTransportOperation { NId = "TO-1" }, ProductErrors.InvalidLogisticStatus);
        p.Fails(new CompleteTransportOperation { NId = "TO-9" }, ProductErrors.UnknownTransportOperation);
    }
}
