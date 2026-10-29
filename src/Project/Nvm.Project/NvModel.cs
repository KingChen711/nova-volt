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
        .Command<CreateProductionSchedule>().Command<CreateDefaultWorkOrder>().Command<SetProcessSchedule>()
        .Command<HandleShiftTimer>().Command<ApplyLineResults>().Command<IngestLineMessages>().Command<SetLinePolling>()
        .Command<CreateQualityCode>().Command<UpdateQualityCode>().Command<DeleteQualityCode>().Command<SetMaterialSupplier>()
        .Command<SetOperationScrapConfig>().Command<DeleteOperationScrapConfig>()
        .Command<HandleLineNonConformances>().Command<HandleLineShrink>()
        .Command<SuspectHoldMtu>().Command<ValidateMtuList>().Command<CreateBulkNonConformance>().Command<SentenceNonConformances>()
        .Command<AddMtuToNonConformance>().Command<TraceNonConformance>().Command<SetNonConformanceOperation>()
        .Command<LinkNonConformanceReason>().Command<NextQualityIdentifier>().Command<SetNonConformanceSupplier>()
        .Command<SetDisassemblyPart>().Command<ListDisassemblyComponents>().Command<DisassembleProduct>().Command<UndoDisassembly>()
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
}

/// <summary>Endpoint của môi trường mà code của team dùng.</summary>
public static class NvEndpoints
{
    /// <summary>Chuỗi kết nối LineDB bằng role chỉ vào được schema trung gian (K11).</summary>
    public const string LineDb = "LineDb";
}
