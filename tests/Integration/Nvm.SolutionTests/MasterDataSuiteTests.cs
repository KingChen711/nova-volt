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
            "Production_005_Defects", "Production_006_PackingEquipment", "Production_007_FinishedGoodsMaterials",
            "Production_008_FinishedGoodsProcesses", "Production_009_MaterialProperties", "Production_010_EquipmentProperties",
            "Production_011_ShippingEquipment", "Production_012_ShippingEquipmentProperties", "Production_013_RouteGroups",
            "MapApp_001_LineCodes", "MapApp_002_LineFailureCodes", "MapApp_003_PackingLineCodes", "MapApp_004_PartAddresses",
            "NvProductionExt_001_WorkOrderLifecycle", "NvProductionExt_002_ProcessSchedules", "NvProductionExt_003_ProductionSchedules",
            "NvProductionExt_004_DefaultWorkOrders", "NvProductionExt_005_InHouseMaterials", "NvProductionExt_006_QualityCodes",
            "NvProductionExt_007_MaterialSuppliers", "NvProductionExt_008_DisassemblyParts", "NvProductionExt_009_ContainerStates",
            "NvProductionExt_010_PackingOperations", "NvProductionExt_011_FinishedGoods", "NvProductionExt_012_FinishedGoodsSchedules",
            "NvProductionExt_013_CustomerTrailerStates", "ShipApp_001_ShipTo", "ShipApp_002_LoadingExceptionCodes",
        ]);

        h.Read<Equipment>().Count.ShouldBe(132);
        h.Read<Equipment>(q => q.Where(e => e.Level == "Area" && e.NId.StartsWith("NV1-B"))).Count.ShouldBe(16);
        h.Read<Material>().Count.ShouldBe(15);
        h.Read<Material>(q => q.Where(m => m.MaterialClass == "InHouse")).Count.ShouldBe(6);
        h.Read<Process>().Count.ShouldBe(20);
        h.Read<ProductionSchedule>().Count.ShouldBe(32);
        h.Read<DefaultWorkOrder>().Count.ShouldBe(40);
        h.Read<SystemTimer>(q => q.Where(t => t.NId.StartsWith("DAILY_WO_"))).Count.ShouldBe(32);
        h.Read<StatusTransition>(q => q.Where(t => t.ToStatus == NvWorkOrderStates.Frozen)).Count.ShouldBe(2);
        h.Read<LineOperationCode>().Count.ShouldBe(6);
        h.Read<LineEquipmentCode>().Count.ShouldBe(26);
        h.Read<LineMaterialCode>().Count.ShouldBe(12);
        h.Read<LineFailureCode>().Count.ShouldBe(4);
        h.Read<Defect>().Count.ShouldBe(5);
        h.Read<QualityCode>().Count.ShouldBe(15);
        h.Read<MaterialSupplier>().Count.ShouldBe(14);
        h.Read<DisassemblyPart>().Count.ShouldBe(4);
        h.Read<MaterialProperty>().Count.ShouldBe(5);
        h.Read<PartAddress>().Count.ShouldBe(4);
        h.Read<PackingOperation>().Count.ShouldBe(4);
        h.Read<StatusTransition>(q => q.Where(t => t.Machine == NvStateMachines.Pallet)).Count.ShouldBe(9);

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
        again.Single(s => s.Suite == "Production_006_PackingEquipment").Log.Count(l => l.EndsWith("Skipped", StringComparison.Ordinal)).ShouldBe(31);
        h.Read<Equipment>().Count.ShouldBe(132);
        h.Read<DefaultWorkOrder>().Count.ShouldBe(40);
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
