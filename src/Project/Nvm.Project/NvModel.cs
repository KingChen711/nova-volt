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
        .Command<CreateProductionSchedule>().Command<CreateDefaultWorkOrder>().Command<SetProcessSchedule>()
        .Command<HandleShiftTimer>();
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
}
