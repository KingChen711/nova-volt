using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Lớp Project (vai team): FB của team và extension app gắn vào base app Production. Mở rộng lớp Product bằng facet,
// pre-check, command mới; không sửa lớp Product.

[FunctionalBlock("FB_MS_Nv", ModelDomain.MasterData)]
public sealed class NvMasterBlock;

[FunctionalBlock("FB_OP_Nv", ModelDomain.OperationalData)]
public sealed class NvOperationBlock;

/// <summary>Extension app của team: command công bố trên Production với tiền tố <c>NvProductionExt_</c>.</summary>
[App(Name, BaseApp = typeof(ProductionApp))]
public sealed class NvProductionExtApp : AppDefinition
{
    public const string Name = "NvProductionExt";

    protected override void Define(PomBuilder pom) => pom
        .Entity<ProductionSchedule>().Entity<DefaultWorkOrder>().Entity<ProcessSchedule>().Entity<WorkOrderShift>()
        .Entity<LineExecution>()
        .Entity<QualityCode>().Entity<MaterialSupplier>().Entity<OperationScrapConfig>().Entity<NonConformanceDetail>().Entity<MtuRunMode>()
        .Entity<NvCounter>().Entity<LineShrinkExecution>().Entity<NonConformanceOperation>().Entity<NonConformanceReasonLink>()
        .Entity<ContainmentIdentifier>().Entity<DisassemblyPart>().Entity<DisassemblyLog>().Entity<NonConformanceListRow>()
        .Entity<PackingOperation>().Entity<PackingDetail>()
        .Command<CreateProductionSchedule>().Command<CreateDefaultWorkOrder>().Command<SetProcessSchedule>()
        .Command<HandleShiftTimer>().Command<ApplyLineResults>().Command<IngestLineMessages>().Command<SetLinePolling>()
        .Command<CreateQualityCode>().Command<UpdateQualityCode>().Command<DeleteQualityCode>().Command<SetMaterialSupplier>()
        .Command<SetOperationScrapConfig>().Command<DeleteOperationScrapConfig>()
        .Command<HandleLineNonConformances>().Command<HandleLineShrink>()
        .Command<SuspectHoldMtu>().Command<ValidateMtuList>().Command<CreateBulkNonConformance>().Command<SentenceNonConformances>()
        .Command<AddMtuToNonConformance>().Command<TraceNonConformance>().Command<SetNonConformanceOperation>()
        .Command<LinkNonConformanceReason>().Command<NextQualityIdentifier>().Command<SetNonConformanceSupplier>()
        .Command<SetDisassemblyPart>().Command<ListDisassemblyComponents>().Command<DisassembleProduct>().Command<UndoDisassembly>()
        .Command<SetPackingOperation>().Command<StartPalletRebuild>().Command<AddToteToRebuiltPallet>().Command<CompletePalletRebuild>()
        .ReadingFunction<NonConformanceList>();
}

public sealed class NvResponse : CommandResponse
{
    public Guid? Id { get; set; }

    public string? NId { get; set; }

    internal static NvResponse Of(Entity item) => new() { Id = item.Id, NId = item.NId };

    internal static NvResponse Fail(int code, string message)
    {
        var response = new NvResponse();
        response.SetError(code, message);
        return response;
    }
}

/// <summary>Mã lỗi của team. Pre-check của team trả 1001 như hệ thật; lỗi handler dùng 9xxx.</summary>
public static class NvErrors
{
    public const int PreCheckFailed = 1001;
    public const int UnknownEquipment = 9001;
    public const int InvalidShiftTime = 9002;
    public const int UnknownSchedule = 9003;
    public const int UnknownProcess = 9004;
    public const int UnknownOperation = 9005;
    public const int UnknownMaterial = 9006;
    public const int UnknownDefect = 9007;
    public const int MissingEndpoint = 9008;

    public const int InvalidQualityCodeKind = 9101;
    public const int DuplicateQualityCode = 9102;
    public const int DuplicateQualityCodeName = 9103;
    public const int UnknownQualityCode = 9104;
    public const int InvalidScrapConfig = 9105;

    public const int UnknownMtu = 9110;
    public const int MtuScrapped = 9111;
    public const int InvalidMtuList = 9112;
    public const int UnknownNonConformance = 9113;
    public const int NonConformanceClosed = 9114;
    public const int MissingDispositionField = 9115;
    public const int InvalidDisposition = 9116;

    public const int NoNonConformance = 9120;
    public const int PartNotAllowed = 9121;
    public const int NotAComponent = 9122;
    public const int UnknownDisassembly = 9123;

    public const int InvalidIdentifierType = 9130;

    public const int InvalidPackingAction = 9201;
    public const int InvalidPallet = 9202;
    public const int UnknownContainer = 9203;
    public const int NotTested = 9204;
    public const int HeldOrScrapped = 9205;
    public const int MaterialMismatch = 9206;
    public const int MissingPalletQuantity = 9207;
    public const int PalletFull = 9208;
    public const int PalletNotComplete = 9209;
    public const int NoStoreAddress = 9210;
    public const int NotAStoreAddress = 9211;
    public const int MissingConfigurationKey = 9212;
    public const int InvalidPackageCode = 9213;
    public const int AlreadyPacked = 9214;

    public const int UnknownTrailer = 9301;
    public const int InvalidTrailerState = 9302;
    public const int TrailerAtDock = 9303;
    public const int TrailerNotAtDock = 9304;
    public const int NoStorageType = 9305;
    public const int UnknownLocation = 9306;
    public const int LocationBusy = 9307;
    public const int MovementOpen = 9308;
    public const int InvalidMovement = 9309;
    public const int MixedTestState = 9310;
    public const int NotInTrailer = 9311;
    public const int WrongRoutePool = 9312;
    public const int NoYardPass = 9313;

    public const int InvalidSalesOrder = 9401;
    public const int UnknownShipment = 9402;
    public const int InvalidShipmentState = 9403;
    public const int NotStored = 9404;
    public const int ItemTooYoung = 9405;
    public const int NotPicked = 9406;
    public const int ShortPick = 9407;
    public const int NoLabelConfig = 9408;
    public const int NoBoxOrder = 9409;
    public const int UnknownSkid = 9410;
    public const int SkidInProgress = 9411;
    public const int NoRouteArea = 9412;
    public const int InvalidBoxLabel = 9413;
    public const int BoxScanned = 9414;
    public const int ExceptionRequired = 9415;
    public const int UnknownExceptionCode = 9416;
    public const int DuplicateException = 9417;
    public const int NoTrailerAtDock = 9418;
    public const int SkidNotStaged = 9419;
    public const int NotConfirmed = 9420;
}

/// <summary>Trạng thái team thêm vào state machine WO bằng dữ liệu.</summary>
public static class NvWorkOrderStates
{
    public const string Frozen = "Frozen";
}

/// <summary>Configuration key của team.</summary>
public static class NvKeys
{
    /// <summary>
    /// Lệch giờ của server so với UTC, tính bằng phút. Giờ đầu ca của timer được hiểu theo giờ server, còn phép so khung ca
    /// lúc tạo WO dùng giờ UTC (lab L08). Thiếu key: 0, tức server chạy UTC.
    /// </summary>
    public const string ServerUtcOffsetMinutes = "Nv.ServerUtcOffsetMinutes";

    /// <summary>
    /// Đường chuyển tin từ LineDB: <c>relay</c> (relay nghe notify, đường ban đầu) hoặc <c>poll</c> (signal rule gọi
    /// <see cref="IngestLineMessages"/>). Thiếu key: <c>relay</c>, lệnh đọc tin không làm gì.
    /// </summary>
    public const string LineTransfer = "Nv.LineTransfer";

    /// <summary>Số dòng tối đa mỗi lần lệnh đọc tin lấy từ LineDB cho một mã bước. Thiếu key: 50.</summary>
    public const string LinePollBatchSize = "Nv.LinePollBatchSize";

    /// <summary>
    /// Chuỗi kết nối SQL mà code thành phẩm dùng để gọi stored procedure. Giá trị là của từng môi trường, không nằm trong repo
    /// (K13). Thiếu key: lệnh và màn dùng procedure báo lỗi (lab L18).
    /// </summary>
    public const string SqlConnection = "Nv.SqlConnection";

    /// <summary>Kiểm bước đo cuối khi chuyển pallet (cất kho). <c>false</c>: bỏ kiểm. Thiếu key: kiểm.</summary>
    public const string EolvValidation = "Nv.EolvValidation";

    /// <summary>Số ngày một lần đo cuối còn hiệu lực; timer hằng ngày đánh dấu pallet quá hạn. Thiếu key: không đánh dấu.</summary>
    public const string EolvRetestAfterDays = "Nv.EolvRetestAfterDays";

    /// <summary>Kiểm hàng bị giữ, bị hủy khi nạp pallet vào trailer kho. <c>false</c>: bỏ kiểm. Thiếu key: kiểm.</summary>
    public const string StorageTrailerHeldCheck = "Nv.StorageTrailerHeldCheck";

    /// <summary>Tiền tố mã gói lẻ. Thiếu key: không kiểm mã.</summary>
    public const string CkdPrefix = "Nv.CkdPrefix";

    /// <summary>Độ dài mã gói lẻ, kể cả tiền tố.</summary>
    public const string CkdLength = "Nv.CkdLength";

    /// <summary>
    /// Số giờ tối thiểu từ lúc làm ra tới lúc hàng họ B được lấy: pallet có hàng trẻ hơn thì không pick được. Thiếu key hoặc 0: không
    /// kiểm (môi trường thiếu key thì không thấy chặn).
    /// </summary>
    public const string MinimumItemAgeHours = "Nv.MinimumItemAgeHours";
}

/// <summary>Endpoint của môi trường mà code của team dùng.</summary>
public static class NvEndpoints
{
    /// <summary>Chuỗi kết nối LineDB bằng role chỉ vào được schema trung gian (K11).</summary>
    public const string LineDb = "LineDb";
}
