using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Bảng đối chiếu mã phía xưởng: tin từ LineDB chỉ mang mã của xưởng (bước, máy, nguyên liệu, lỗi), MOM dịch sang đối
// tượng của mình trước khi làm gì khác. Bảng nằm ở app nghiệp vụ riêng của team (MapApp), domain master data.

[FunctionalBlock("FB_MS_Map", ModelDomain.MasterData)]
public sealed class MapMasterBlock;

/// <summary>App giữ bảng đối chiếu mã phía xưởng. App độc lập, không gắn vào Production.</summary>
[App(Name)]
public sealed class MapApp : AppDefinition
{
    public const string Name = "MapApp";

    protected override void Define(PomBuilder pom) => pom
        .Entity<LineOperationCode>().Entity<LineEquipmentCode>().Entity<LineMaterialCode>().Entity<LineFailureCode>()
        .Command<SetLineOperationCode>().Command<SetLineEquipmentCode>().Command<SetLineMaterialCode>().Command<SetLineFailureCode>();
}

/// <summary>Mã bước phía xưởng (NId) → bước của process. Một mã dùng chung cho mọi line: chỉ mã máy mang số line.</summary>
[Entity<MapMasterBlock>]
public sealed class LineOperationCode : Entity
{
    [Mandatory]
    public string? OperationNId { get; set; }
}

/// <summary>Mã máy phía xưởng (NId, dạng <c>&lt;mã máy&gt;_&lt;số line&gt;</c>) → equipment.</summary>
[Entity<MapMasterBlock>]
public sealed class LineEquipmentCode : Entity
{
    [Mandatory]
    public string? EquipmentNId { get; set; }

    public int LineNumber { get; set; }
}

/// <summary>Mã nguyên liệu phía xưởng (NId, thường là một câu mô tả) → material.</summary>
[Entity<MapMasterBlock>]
public sealed class LineMaterialCode : Entity
{
    [Mandatory]
    public string? MaterialNId { get; set; }
}

/// <summary>Mã lỗi máy gửi kèm tin KO (NId) → defect. Dùng ở bước tạo NC từ tin (R6).</summary>
[Entity<MapMasterBlock>]
public sealed class LineFailureCode : Entity
{
    [Mandatory]
    public string? DefectNId { get; set; }
}

public sealed class MapResponse : CommandResponse
{
    public string? NId { get; set; }

    internal static MapResponse Fail(int code, string message)
    {
        var response = new MapResponse();
        response.SetError(code, message);
        return response;
    }
}

internal static class LineCodeTables
{
    /// <summary>Thêm hoặc sửa một dòng đối chiếu; đối tượng đích phải có.</summary>
    public static MapResponse Upsert<T>(IPlatform platform, string nid, Action<T> set)
        where T : Entity, new()
    {
        var row = platform.Query<T>().SingleOrDefault(r => r.NId == nid) ?? platform.Create<T>();
        row.NId = nid;
        set(row);
        platform.Submit(row);
        return new MapResponse { NId = nid };
    }
}

[Command<MapMasterBlock>]
public sealed class SetLineOperationCode : ICommand<MapResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? OperationNId { get; init; }
}

internal sealed class SetLineOperationCodeHandler : ICommandHandler<SetLineOperationCode, MapResponse>
{
    public MapResponse Handle(SetLineOperationCode command, IPlatform platform) =>
        platform.ProjectionQuery<ProcessOperation>().Any(o => o.OperationNId == command.OperationNId)
            ? LineCodeTables.Upsert<LineOperationCode>(platform, command.NId!, r => r.OperationNId = command.OperationNId)
            : MapResponse.Fail(NvErrors.UnknownOperation, $"No process has operation {command.OperationNId}.");
}

[Command<MapMasterBlock>]
public sealed class SetLineEquipmentCode : ICommand<MapResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? EquipmentNId { get; init; }

    public int LineNumber { get; init; }
}

internal sealed class SetLineEquipmentCodeHandler : ICommandHandler<SetLineEquipmentCode, MapResponse>
{
    public MapResponse Handle(SetLineEquipmentCode command, IPlatform platform) =>
        platform.Query<Equipment>().Any(e => e.NId == command.EquipmentNId)
            ? LineCodeTables.Upsert<LineEquipmentCode>(platform, command.NId!, r =>
            {
                r.EquipmentNId = command.EquipmentNId;
                r.LineNumber = command.LineNumber;
            })
            : MapResponse.Fail(NvErrors.UnknownEquipment, $"Equipment {command.EquipmentNId} does not exist.");
}

[Command<MapMasterBlock>]
public sealed class SetLineMaterialCode : ICommand<MapResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }
}

internal sealed class SetLineMaterialCodeHandler : ICommandHandler<SetLineMaterialCode, MapResponse>
{
    public MapResponse Handle(SetLineMaterialCode command, IPlatform platform) =>
        platform.Query<Material>().Any(m => m.NId == command.MaterialNId)
            ? LineCodeTables.Upsert<LineMaterialCode>(platform, command.NId!, r => r.MaterialNId = command.MaterialNId)
            : MapResponse.Fail(NvErrors.UnknownMaterial, $"Material {command.MaterialNId} does not exist.");
}

[Command<MapMasterBlock>]
public sealed class SetLineFailureCode : ICommand<MapResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? DefectNId { get; init; }
}

internal sealed class SetLineFailureCodeHandler : ICommandHandler<SetLineFailureCode, MapResponse>
{
    public MapResponse Handle(SetLineFailureCode command, IPlatform platform) =>
        platform.ProjectionQuery<Defect>().Any(d => d.NId == command.DefectNId)
            ? LineCodeTables.Upsert<LineFailureCode>(platform, command.NId!, r => r.DefectNId = command.DefectNId)
            : MapResponse.Fail(NvErrors.UnknownDefect, $"Defect {command.DefectNId} does not exist.");
}
