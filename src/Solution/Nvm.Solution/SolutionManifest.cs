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
    public const string Yard = "yard";
    public const string Shipping = "shipping";

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

        // Foundation (cấu hình nền) và bảng đối chiếu mã phía xưởng: chỉ quản trị.
        solution.Apps.Add(new AppConfiguration(FoundationAppName) { RestrictCommands = true });
        solution.Apps.Add(new AppConfiguration(MapApp.Name) { RestrictCommands = true });

        // Tin từ xưởng xử lý tuần tự: hai lô tin của cùng một serial không chạy chồng nhau.
        solution.WorkerRoles[LineMessageHandler.WorkerRoleName] = new WorkerRoleConfiguration { Sequential = true };

        var production = new AppConfiguration(ProductionApp.Name) { RestrictCommands = true };
        production.EnabledPreChecks.UnionWith(ProductionPreChecks);

        // Master data (equipment, material, BoP, PS, DWO, trạng thái): chỉ quản trị, nên không ghi ở đây.
        production.Allow(
            [nameof(ReleaseWorkOrder), nameof(SetWorkOrderStatus), nameof(AbortWorkOrder), nameof(EditWorkOrder), nameof(DeleteWorkOrder),
             nameof(CreateWorkOrderFromProcess), Ext + nameof(HandleShiftTimer), Ext + nameof(ApplyLineResults)],
            LineLead);
        production.Allow(
            [nameof(CreateMtu), nameof(EditMtu), nameof(MoveMtu), nameof(SplitMtu), nameof(CreateMtua), nameof(AssembleIntoMtua),
             nameof(RemoveFromMtua), nameof(RecordWorkOrderOutput), nameof(RecordOperationProgress), nameof(RecordConsumption),
             nameof(AttachMtuToBuffer), nameof(DetachMtuFromBuffer), nameof(LinkMtuToBuffer)],
            LineOperator, LineLead);
        production.Allow([nameof(CreateNonConformance), nameof(HoldMtus), Ext + nameof(SuspectHoldMtu)], LineOperator, LineLead, Quality);
        production.Allow([Ext + nameof(HandleLineNonConformances), Ext + nameof(HandleLineShrink)], LineLead);
        production.Allow(
            [Ext + nameof(ValidateMtuList), Ext + nameof(CreateBulkNonConformance), Ext + nameof(SentenceNonConformances),
             Ext + nameof(AddMtuToNonConformance), Ext + nameof(TraceNonConformance), Ext + nameof(SetNonConformanceOperation),
             Ext + nameof(LinkNonConformanceReason), Ext + nameof(NextQualityIdentifier), Ext + nameof(ListDisassemblyComponents),
             Ext + nameof(DisassembleProduct), Ext + nameof(UndoDisassembly), Ext + nameof(SetNonConformanceSupplier),
             Ext + nameof(DeclareSupplierIssue)],
            Quality);
        production.Allow(
            [nameof(SetNonConformanceStatus), nameof(ReleaseMtus), nameof(ImportContainmentRequest), nameof(ReleaseContainmentRequest),
             nameof(CloseContainmentRequest)],
            Quality);
        production.Allow(
            [nameof(CreateLogisticRequest), nameof(AcceptMaterialRequest), nameof(StartTransportOperation), nameof(CompleteTransportOperation), nameof(CancelTransportOperation)],
            Logistics);

        // Đóng gói: dựng lại pallet sau bước đo cuối là màn của xưởng.
        production.Allow(
            [Ext + nameof(StartPalletRebuild), Ext + nameof(AddToteToRebuiltPallet), Ext + nameof(CompletePalletRebuild)],
            LineOperator, LineLead);
        solution.Apps.Add(production);

        // Thành phẩm: cất kho, gỡ pallet, gói lẻ cho người kho; kiểm pallet cho chất lượng.
        var ship = new AppConfiguration(ShipApp.Name) { RestrictCommands = true };
        ship.Allow(
            [nameof(StorePallet), nameof(UnloadTotesFromPallet), nameof(OpenCkdPackage), nameof(AddItemToCkdPackage),
             nameof(RemoveItemFromCkdPackage)],
            Shipping);
        ship.Allow([nameof(PalletStatusValidation)], Quality, Shipping);

        // Xuất hàng: người kho và người ở dock (một role ở NovaVolt). Nhận đơn, dữ liệu đặt hàng, phản hồi ASN là tin từ hệ ngoài,
        // cấu hình nơi nhận và danh mục ngoại lệ là master data: chỉ quản trị.
        ship.Allow(
            [nameof(ReleasePickListGroup), nameof(PickPallet), nameof(UnpickPallet), nameof(CompletePickList), nameof(PrintSkidLabels),
             nameof(PrintBoxLabels), nameof(ScanSkidLabel), nameof(StagePallet), nameof(ScanBoxLabel), nameof(CheckTruckLoading),
             nameof(AddLoadingExceptionEntry), nameof(DeleteLoadingExceptionEntry), nameof(ConfirmTruckLoad)],
            Shipping);
        solution.Apps.Add(ship);

        // Trailer kho: người ở dock nạp, chốt, dỡ; người điều phối và người kéo xe làm yêu cầu kéo.
        var yard = new AppConfiguration(YardApp.Name) { RestrictCommands = true };
        yard.Allow(
            [nameof(CreateTrailer), nameof(ArriveEmptyTrailer), nameof(SetTrailerAsStorage), nameof(UnsetTrailerAsStorage),
             nameof(CallStorageTrailer), nameof(CreateTrailerMovementRequest), nameof(UpdateTrailerMovement)],
            Yard);
        yard.Allow(
            [nameof(LoadPalletIntoStorageTrailer), nameof(CompleteStorageTrailerLoad), nameof(UnloadStorageTrailer),
             nameof(UnloadPalletFromStorageTrailer)],
            Yard, Shipping);

        // Xe của khách: nhận ở cổng, kéo vào và ra dock, yard pass, cho xe rời.
        yard.Allow(
            [nameof(ArriveCustomerTrailer), nameof(MoveTrailerIn), nameof(MoveTrailerOut), nameof(IssueYardPass), nameof(DepartCustomerTrailer)],
            Yard, Shipping);

        // Xe linh kiện: cổng, dock, người kéo xe. Lịch từ cổng nhà cung cấp là tin từ hệ ngoài nên chỉ quản trị.
        yard.Allow(
            [nameof(ArriveComponentTrailer), nameof(RegisterUnplannedTrailer), nameof(GeneratePlacard), nameof(CallEmptyTrailer),
             nameof(ChangeMovementDestination), nameof(ReceiveSkid), nameof(CompleteUnloading), nameof(CompleteLocalSchedule),
             nameof(ReportTrailerReloaded), nameof(IssueTrailerYardPass), nameof(DepartTrailer), nameof(CheckTransferSkid), nameof(TransferSkids)],
            Yard);
        solution.Apps.Add(yard);

        // Gọi hàng: khu gọi và nạp lô; người chở nhận, quét, hoàn tất; người nhận hàng nhận nguyên liệu đặt thẳng. Đơn từ ERP và route: chỉ quản trị.
        var pull = new AppConfiguration(PullApp.Name) { RestrictCommands = true };
        pull.Allow([nameof(CallMaterial), nameof(CancelPullTask), nameof(LoadLot), nameof(UnloadLot)], LineOperator, LineLead);
        pull.Allow([nameof(AcceptPullTask), nameof(RecordLot), nameof(CompletePullTask)], Logistics);
        pull.Allow([nameof(ReceiveDirectMaterial)], Logistics, Yard);
        solution.Apps.Add(pull);
        return solution;
    }
}
