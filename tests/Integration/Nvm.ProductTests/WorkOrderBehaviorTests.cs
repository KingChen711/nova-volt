using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.ProductTests;

/// <summary>Work Order: tạo từ process, trạng thái theo dữ liệu, ghi sản phẩm, tiến độ bước, trừ nguyên liệu.</summary>
public sealed class WorkOrderBehaviorTests
{
    [Fact]
    public async Task Work_order_from_process_copies_operations_planned_machines_and_materials()
    {
        var p = await ProductHarness.CreateAsync();

        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process, ProductionType = "FlexibleBatch" });

        var wo = p.Read<WorkOrder>().ShouldHaveSingleItem();
        wo.Status.ShouldBe(WorkOrderStates.Edit);
        wo.ProductMaterialNId.ShouldBe(ProductHarness.Module);
        wo.BopNId.ShouldBe(ProductHarness.Process);
        p.Read<WorkOrderOperation>(q => q.OrderBy(o => o.Sequence)).Select(o => (o.OperationNId, o.Status)).ShouldBe([("OP10", "Open"), ("OP20", "Open")]);
        p.Read<WorkOrderOperationEquipment>().ShouldHaveSingleItem().EquipmentNId.ShouldBe(ProductHarness.Machine);
        p.Read<WorkOrderOperationMaterial>().ShouldHaveSingleItem().Quantity.ShouldBe(0.25m);
        p.Fails(new CreateWorkOrderFromProcess { NId = "WO-2", ProcessNId = "missing" }, ProductErrors.UnknownProcess);
    }

    [Fact]
    public async Task Status_follows_the_data_driven_machine_and_raises_an_event_after_commit()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process });

        p.Fails(new SetWorkOrderStatus { NId = "WO-1", Status = WorkOrderStates.Complete }, 7101);
        p.Ok(new ReleaseWorkOrder { NId = "WO-1" });
        p.Ok(new SetWorkOrderStatus { NId = "WO-1", Status = WorkOrderStates.Active });

        p.Read<WorkOrder>().Single().Status.ShouldBe(WorkOrderStates.Active);
        p.Events.Published.Select(e => e.Event).OfType<WorkOrderStatusChanged>().Select(e => e.ToStatus)
            .ShouldBe([WorkOrderStates.Edit, WorkOrderStates.New, WorkOrderStates.Active]);
    }

    [Fact]
    public async Task Frozen_added_as_data_and_abort_from_frozen_needs_the_assigned_role()
    {
        // Phía dự án thêm Frozen và gán role cho Frozen -> Aborted, chỉ bằng dữ liệu.
        var p = await ProductHarness.CreateAsync();
        p.Executor.Execute("Foundation", new DefineStatus { Machine = WorkOrderStates.Machine, Status = "Frozen" });
        p.Executor.Execute("Foundation", new DefineTransition { Machine = WorkOrderStates.Machine, FromStatus = WorkOrderStates.Active, ToStatus = "Frozen" });
        p.Executor.Execute("Foundation", new DefineTransition { Machine = WorkOrderStates.Machine, FromStatus = "Frozen", ToStatus = WorkOrderStates.Aborted });
        p.Executor.Execute("Foundation", new AssignTransitionRole
        {
            Machine = WorkOrderStates.Machine,
            FromStatus = "Frozen",
            ToStatus = WorkOrderStates.Aborted,
            Role = "PlanningAdmin",
        });
        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process });
        p.Ok(new ReleaseWorkOrder { NId = "WO-1" });
        p.Ok(new SetWorkOrderStatus { NId = "WO-1", Status = WorkOrderStates.Active });
        p.Ok(new SetWorkOrderStatus { NId = "WO-1", Status = "Frozen" });

        p.Fails(new AbortWorkOrder { NId = "WO-1" }, 7102, ProductHarness.Caller("Operator"));
        p.Read<WorkOrder>().Single().Status.ShouldBe("Frozen");
        p.Ok(new AbortWorkOrder { NId = "WO-1" }, ProductHarness.Caller("PlanningAdmin"));
        p.Read<WorkOrder>().Single().Status.ShouldBe(WorkOrderStates.Aborted);
    }

    [Fact]
    public async Task Forced_status_skips_transitions_but_not_unknown_statuses()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process });

        p.Ok(new SetWorkOrderStatusForced { NId = "WO-1", Status = WorkOrderStates.Complete });
        p.Fails(new SetWorkOrderStatusForced { NId = "WO-1", Status = "Teleported" }, 7101);

        p.Read<WorkOrder>().Single().Status.ShouldBe(WorkOrderStates.Complete);
        p.Events.Published.Select(e => e.Event).OfType<WorkOrderStatusChanged>().Last().Forced.ShouldBeTrue();
    }

    [Fact]
    public async Task Edit_changes_plan_and_delete_is_soft_so_the_name_can_be_reused()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process, Quantity = 10 });
        p.Ok(new EditWorkOrder { NId = "WO-1", Quantity = 12 });
        p.Read<WorkOrder>().Single().Quantity.ShouldBe(12m);

        p.Ok(new DeleteWorkOrder { NId = "WO-1" });
        p.Read<WorkOrder>().ShouldBeEmpty();
        p.Read<WorkOrderOperation>().ShouldBeEmpty();
        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process });
    }

    [Fact]
    public async Task Recording_output_does_not_add_to_the_produced_quantity_unless_an_increment_is_given()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process });

        p.Ok(new RecordWorkOrderOutput { WorkOrderNId = "WO-1", MtuNId = "M-1", CreateMtu = true, EquipmentNId = ProductHarness.Machine });
        p.Read<WorkOrder>().Single().ProducedQuantity.ShouldBe(0m);

        p.Ok(new CreateMtu { NId = "M-2", MaterialNId = ProductHarness.Module });
        p.Ok(new RecordWorkOrderOutput { WorkOrderNId = "WO-1", MtuNId = "M-2", QuantityIncrement = 1 });
        p.Read<WorkOrder>().Single().ProducedQuantity.ShouldBe(1m);

        p.Read<WorkOrderOutput>().Select(o => o.MtuNId).Order().ShouldBe(["M-1", "M-2"]);
        p.Read<MtuHistory>(q => q.Where(h => h.MtuNId == "M-1")).ShouldHaveSingleItem().WorkOrderNId.ShouldBe("WO-1");
        p.Fails(new RecordWorkOrderOutput { WorkOrderNId = "WO-1", MtuNId = "M-404" }, ProductErrors.UnknownMtu);
    }

    [Fact]
    public async Task Operation_progress_sets_the_step_status_and_machine()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process });

        p.Ok(new RecordOperationProgress { WorkOrderOperationNId = "WO-1:OP10", Status = "Partial", EquipmentNId = ProductHarness.Machine });
        p.Fails(new RecordOperationProgress { WorkOrderOperationNId = "WO-1:OP10", Status = "Done" }, ProductErrors.UnknownWorkOrderOperation);

        var step = p.Read<WorkOrderOperation>(q => q.Where(o => o.NId == "WO-1:OP10")).Single();
        step.Status.ShouldBe("Partial");
        step.ActualEquipmentNId.ShouldBe(ProductHarness.Machine);
    }

    [Fact]
    public async Task Consumption_lowers_the_lot_and_writes_assemble_genealogy_on_the_product()
    {
        var p = await ProductHarness.CreateAsync();
        p.Ok(new CreateWorkOrderFromProcess { NId = "WO-1", ProcessNId = ProductHarness.Process });
        p.Ok(new CreateMtu { NId = "LOT-1", MaterialNId = ProductHarness.Paste, Quantity = 1m });
        p.Ok(new RecordWorkOrderOutput { WorkOrderNId = "WO-1", MtuNId = "M-1", CreateMtu = true });

        p.Ok(new RecordConsumption { WorkOrderNId = "WO-1", OperationNId = "OP10", ProductMtuNId = "M-1", ComponentMtuNId = "LOT-1", Quantity = 0.25m });
        p.Fails(new RecordConsumption { WorkOrderNId = "WO-1", ProductMtuNId = "M-1", ComponentMtuNId = "LOT-1", Quantity = 5m },
            ProductErrors.InsufficientQuantity);

        p.Mtu("LOT-1").Quantity.ShouldBe(0.75m);
        var assemble = p.Read<MtuHistory>(q => q.Where(h => h.MtuNId == "M-1" && h.Action == MtuHistoryActions.Assemble)).ShouldHaveSingleItem();
        assemble.ComponentMtuNId.ShouldBe("LOT-1");
        assemble.Quantity.ShouldBe(0.25m);
        p.Read<MaterialConsumption>().ShouldHaveSingleItem().OperationNId.ShouldBe("OP10");
    }
}
