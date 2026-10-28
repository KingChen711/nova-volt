using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Product;
using Nvm.Project;

namespace Nvm.Solution;

/// <summary>
/// Manifest của solution: model gồm những lớp nào, command settings của từng app (pre-check bật ở đâu), role được gọi
/// command nào. Host và test dùng chung, nên hành vi trong test là hành vi lúc chạy thật.
/// </summary>
public static class SolutionManifest
{
    public const string AdministratorRole = "mom-admin";

    public const string FoundationAppName = "Foundation";

    public const string LineOperator = "line-operator";
    public const string LineLead = "line-lead";
    public const string Quality = "quality";
    public const string Logistics = "logistics";

    private const string Ext = NvProductionExtApp.Name + "_";

    /// <summary>Pre-check của team bật trên base app Production.</summary>
    public static readonly IReadOnlyList<string> ProductionPreChecks =
    [
        nameof(CheckUserHasCorrectRoleToAbort), nameof(CheckUserHasCorrectRoleToRelease),
        nameof(CheckUserHasCorrectRoleToChangeStatus), nameof(PreventTwoActiveWorkOrdersOnMachine), nameof(PreventWODeletionIfStarted),
    ];

    public static ModelRegistry BuildRegistry() =>
        ModelRegistry.Build(typeof(FoundationApp).Assembly, typeof(ProductionApp).Assembly, typeof(NvProductionExtApp).Assembly);

    /// <summary>Cấu hình của plant: role quản trị, pre-check và quyền gọi command trên Production và Foundation.</summary>
    public static SolutionConfiguration Configure(string plant = "NV1")
    {
        var solution = new SolutionConfiguration { Plant = plant, AdministratorRole = AdministratorRole };

        // Foundation (cấu hình nền): chỉ quản trị.
        solution.Apps.Add(new AppConfiguration(FoundationAppName) { RestrictCommands = true });

        var production = new AppConfiguration(ProductionApp.Name) { RestrictCommands = true };
        production.EnabledPreChecks.UnionWith(ProductionPreChecks);

        // Master data (equipment, material, BoP, PS, DWO, trạng thái): chỉ quản trị, nên không ghi ở đây.
        production.Allow(
            [nameof(ReleaseWorkOrder), nameof(SetWorkOrderStatus), nameof(AbortWorkOrder), nameof(EditWorkOrder), nameof(DeleteWorkOrder),
             nameof(CreateWorkOrderFromProcess), Ext + nameof(HandleShiftTimer)],
            LineLead);
        production.Allow(
            [nameof(CreateMtu), nameof(EditMtu), nameof(MoveMtu), nameof(SplitMtu), nameof(CreateMtua), nameof(AssembleIntoMtua),
             nameof(RemoveFromMtua), nameof(RecordWorkOrderOutput), nameof(RecordOperationProgress), nameof(RecordConsumption),
             nameof(AttachMtuToBuffer), nameof(DetachMtuFromBuffer)],
            LineOperator, LineLead);
        production.Allow([nameof(CreateNonConformance), nameof(HoldMtus)], LineOperator, LineLead, Quality);
        production.Allow(
            [nameof(SetNonConformanceStatus), nameof(ReleaseMtus), nameof(ImportContainmentRequest), nameof(ReleaseContainmentRequest),
             nameof(CloseContainmentRequest)],
            Quality);
        production.Allow(
            [nameof(CreateLogisticRequest), nameof(ReleaseLogisticRequest), nameof(MoveLogisticRequestWithinFlow), nameof(CompleteLogisticRequest)],
            Logistics);
        solution.Apps.Add(production);
        return solution;
    }
}
