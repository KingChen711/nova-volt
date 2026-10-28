using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.SolutionTests;

/// <summary>Pre-check của team trên WO và quyền gọi command theo role (manifest của solution).</summary>
public sealed class TeamRulesTests
{
    private static readonly CallerIdentity Lead = SolutionHarness.Caller("lead.nv1", "line-operator", "line-lead");
    private static readonly CallerIdentity Operator = SolutionHarness.Caller("op.nv1", "line-operator");

    [Fact]
    public async Task Second_work_order_cannot_go_active_on_a_machine_that_already_runs_the_same_operation()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        foreach (var nid in new[] { "WO-A", "WO-B" })
        {
            h.Run(new CreateWorkOrderFromProcess { NId = nid, ProcessNId = "Coat_LN1" }, Lead).StatusCode.ShouldBe(200);
            h.Run(new ReleaseWorkOrder { NId = nid }, Lead).StatusCode.ShouldBe(200);
        }

        h.Run(new SetWorkOrderStatus { NId = "WO-A", Status = WorkOrderStates.Active }, Lead).StatusCode.ShouldBe(200);
        var refused = h.Run(new SetWorkOrderStatus { NId = "WO-B", Status = WorkOrderStates.Active }, Lead);

        refused.StatusCode.ShouldBe(400);
        refused.Body.ToJsonString().ShouldContain("PreventTwoActiveWorkOrdersOnMachine");
        h.Read<WorkOrder>(q => q.Where(w => w.NId == "WO-B")).Single().Status.ShouldBe(WorkOrderStates.New);
    }

    [Fact]
    public async Task Started_work_order_cannot_be_deleted_until_it_is_back_in_edit()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        h.Run(new CreateWorkOrderFromProcess { NId = "WO-A", ProcessNId = "Coat_LN1" }, Lead);
        h.Run(new ReleaseWorkOrder { NId = "WO-A" }, Lead);
        h.Run(new SetWorkOrderStatus { NId = "WO-A", Status = WorkOrderStates.Active }, Lead);

        var refused = h.Run(new DeleteWorkOrder { NId = "WO-A" }, Lead);
        refused.StatusCode.ShouldBe(400);
        refused.Body.ToJsonString().ShouldContain("Work Order has been previously activated, cannot be deleted.");

        h.Run(new SetWorkOrderStatus { NId = "WO-A", Status = WorkOrderStates.Edit }, Lead).StatusCode.ShouldBe(200);
        h.Run(new DeleteWorkOrder { NId = "WO-A" }, Lead).StatusCode.ShouldBe(200);
        h.Read<WorkOrder>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Commands_follow_the_roles_of_the_manifest_and_the_administrator_may_call_everything()
    {
        var h = await SolutionHarness.WithMasterDataAsync();

        h.Run(new CreateWorkOrderFromProcess { NId = "WO-A", ProcessNId = "Coat_LN1" }, Operator).StatusCode.ShouldBe(403);
        h.Run(new CreateWorkOrderFromProcess { NId = "WO-A", ProcessNId = "Coat_LN1" }, Lead).StatusCode.ShouldBe(200);
        h.Run(new CreateMtu { NId = "COIL-1", MaterialNId = "NV-WIP-ELEC-A", Quantity = 100 }, Operator).StatusCode.ShouldBe(200);
        h.Run(new SetNonConformanceStatus { NId = "NC-1", Status = NonConformanceStatus.Scrap }, Operator).StatusCode.ShouldBe(403);
        h.Run(new CreateEquipment { NId = "NV1-X", Level = "Area", ParentNId = "NV1-B1" }, Lead).StatusCode.ShouldBe(403);
        h.Run(new CreateEquipment { NId = "NV1-X", Level = "Area", ParentNId = "NV1-B1" }, SolutionHarness.Caller("admin.nv1", "mom-admin"))
            .StatusCode.ShouldBe(200);
        h.Run(new SetConfigurationKey { NId = "Nv.Test", Val = "1" }, Lead, app: "Foundation").StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task Shift_timer_runs_under_the_system_caller_without_the_team_pre_checks()
    {
        // Timer gọi command qua app Production dưới người gọi hệ thống; lệnh đổi trạng thái bên trong đi bằng CallCommand.
        var h = await SolutionHarness.WithMasterDataAsync();

        h.RunClockUntil(new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero));

        h.Read<WorkOrder>(q => q.Where(w => w.Status == WorkOrderStates.Active)).Count.ShouldBe(16);
        h.Traces.Records.ShouldNotContain(r => r.Message.Contains("Pre-check", StringComparison.Ordinal));
    }
}
