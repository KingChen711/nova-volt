using Nvm.Foundation;
using Nvm.Product;
using Nvm.Project;
using Nvm.Solution.Suites;

namespace Nvm.SolutionTests;

/// <summary>DoD R4: nạp master data NV1 từ DB trống bằng suite; runner theo cách của Tester Tool.</summary>
public sealed class MasterDataSuiteTests
{
    [Fact]
    public async Task Master_data_loads_from_an_empty_database_and_the_next_deploy_skips_every_suite()
    {
        var h = await SolutionHarness.CreateAsync();

        var first = h.Deploy();
        first.Select(s => s.Status).ShouldAllBe(s => s == SuiteStatus.Executed);
        first.Select(s => s.Suite).ShouldBe([
            "Production_001_Equipment", "Production_002_Materials", "Production_003_Processes", "Production_004_WorkOrderStates",
            "MapApp_001_LineCodes", "NvProductionExt_001_WorkOrderLifecycle", "NvProductionExt_002_ProcessSchedules", "NvProductionExt_003_ProductionSchedules",
            "NvProductionExt_004_DefaultWorkOrders", "NvProductionExt_005_InHouseMaterials",
        ]);

        h.Read<Equipment>().Count.ShouldBe(98);
        h.Read<Equipment>(q => q.Where(e => e.Level == "Area" && e.NId.StartsWith("NV1-B"))).Count.ShouldBe(16);
        h.Read<Material>().Count.ShouldBe(14);
        h.Read<Material>(q => q.Where(m => m.MaterialClass == "InHouse")).Count.ShouldBe(6);
        h.Read<Process>().Count.ShouldBe(16);
        h.Read<ProductionSchedule>().Count.ShouldBe(32);
        h.Read<DefaultWorkOrder>().Count.ShouldBe(32);
        h.Read<SystemTimer>(q => q.Where(t => t.NId.StartsWith("DAILY_WO_"))).Count.ShouldBe(32);
        h.Read<StatusTransition>(q => q.Where(t => t.ToStatus == NvWorkOrderStates.Frozen)).Count.ShouldBe(2);
        h.Read<LineOperationCode>().Count.ShouldBe(4);
        h.Read<LineEquipmentCode>().Count.ShouldBe(16);
        h.Read<LineMaterialCode>().Count.ShouldBe(11);

        h.Deploy().Select(s => s.Status).ShouldAllBe(s => s == SuiteStatus.Skipped);
    }

    [Fact]
    public async Task Rerunning_every_suite_reads_by_key_first_and_creates_no_duplicates()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        File.Delete(h.HistoryFile);

        var again = h.Deploy();

        again.Select(s => s.Status).ShouldAllBe(s => s == SuiteStatus.Executed);
        again.Single(s => s.Suite == "Production_001_Equipment").Log.Count(l => l.EndsWith("Skipped", StringComparison.Ordinal)).ShouldBe(98);
        h.Read<Equipment>().Count.ShouldBe(98);
        h.Read<DefaultWorkOrder>().Count.ShouldBe(32);
    }

    [Fact]
    public async Task Runner_passes_an_expected_failure_and_fails_on_a_wrong_assert()
    {
        var h = await SolutionHarness.WithMasterDataAsync();

        var result = SuiteRunner.Run(Path.Combine(SolutionHarness.Fixtures(), "runner-check.suite.json"), h.Target());

        result.Passed.ShouldBeFalse();
        result.Log.ShouldBe([
            "Step [Checks/RejectedCreate] Passed",
            "Step [Checks/ReadModule] Passed",
            "Step [Checks/WrongUnit] Failed - assert value[0].UnitOfMeasure: expected \"kg\", got \"pc\"",
        ]);
    }
}
