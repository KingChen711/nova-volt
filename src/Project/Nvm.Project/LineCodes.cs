using System.Globalization;
using System.Text.Json;
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
        .Entity<LineOperationCode>().Entity<LineEquipmentCode>().Entity<LineMaterialCode>().Entity<LineFailureCode>().Entity<PartAddress>()
        .Entity<Kanban>().Entity<UomConversion>()
        .Command<SetLineOperationCode>().Command<SetLineEquipmentCode>().Command<SetLineMaterialCode>().Command<SetLineFailureCode>()
        .Command<SetPartAddress>().Command<SavePartAddress>().Command<DeletePartAddress>().Command<ImportKanbans>().Command<SetKanban>().Command<SetUomConversion>();
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

/// <summary>
/// Địa chỉ của một part: ô kho cất thành phẩm (store address) theo kanban. Màn cất kho chỉ nhận ô có trong bảng này. NId =
/// <c>&lt;material&gt;|&lt;store address&gt;</c>. Có route thì part gọi được từ khu sản xuất.
/// </summary>
[Entity<MapMasterBlock>]
public sealed class PartAddress : Entity
{
    [Mandatory]
    public string? MaterialNId { get; set; }

    public string? KanbanNId { get; set; }

    /// <summary>Equipment của ô kho.</summary>
    [Mandatory]
    public string? StoreAddress { get; set; }

    /// <summary>Ô giao ở line.</summary>
    public string? DeliveryAddress { get; set; }

    public string? DeliveryRouteNId { get; set; }

    /// <summary>Dạng <c>yyyy-MM-dd</c>.</summary>
    public string? EffectiveFrom { get; set; }

    /// <summary>Dạng <c>yyyy-MM-dd</c>; rỗng là không hết hạn.</summary>
    public string? EffectiveTo { get; set; }
}

[Command<MapMasterBlock>]
public sealed class SetPartAddress : ICommand<MapResponse>
{
    [Mandatory]
    public string? MaterialNId { get; init; }

    [Mandatory]
    public string? StoreAddress { get; init; }

    public string? KanbanNId { get; init; }

    public string? DeliveryAddress { get; init; }

    public string? DeliveryRouteNId { get; init; }

    public string? EffectiveFrom { get; init; }

    public string? EffectiveTo { get; init; }
}

internal sealed class SetPartAddressHandler : ICommandHandler<SetPartAddress, MapResponse>
{
    public MapResponse Handle(SetPartAddress command, IPlatform platform)
    {
        if (!platform.Query<Material>().Any(m => m.NId == command.MaterialNId))
        {
            return MapResponse.Fail(NvErrors.UnknownMaterial, $"Material {command.MaterialNId} does not exist.");
        }

        return platform.Query<Equipment>().Any(e => e.NId == command.StoreAddress)
            ? LineCodeTables.Upsert<PartAddress>(platform, command.MaterialNId + "|" + command.StoreAddress, r =>
            {
                r.MaterialNId = command.MaterialNId;
                r.StoreAddress = command.StoreAddress;
                r.KanbanNId = command.KanbanNId;
                r.DeliveryAddress = command.DeliveryAddress;
                r.DeliveryRouteNId = command.DeliveryRouteNId;
                r.EffectiveFrom = command.EffectiveFrom;
                r.EffectiveTo = command.EffectiveTo;
            })
            : MapResponse.Fail(NvErrors.UnknownEquipment, $"Equipment {command.StoreAddress} does not exist.");
    }
}

/// <summary>Kanban của một part, chép từ hệ ngoài. NId = <c>part|dock|nhà cung cấp|ngày hiệu lực</c>: một part có thể có nhiều dòng.</summary>
[Entity<MapMasterBlock>]
public sealed class Kanban : Entity
{
    public string? MaterialNId { get; set; }

    public string? DockCode { get; set; }

    public string? SupplierCode { get; set; }

    public string? KanbanNo { get; set; }

    public decimal QuantityPerContainer { get; set; }

    /// <summary>Dạng <c>yyyy-MM-dd</c>.</summary>
    public string? EffectiveFrom { get; set; }

    /// <summary>Dạng <c>yyyy-MM-dd</c>; rỗng là không hết hạn.</summary>
    public string? EffectiveTo { get; set; }
}

public static class UomSystems
{
    public const string Receiving = "Receiving";
}

/// <summary>Quy đổi đơn vị của material theo hệ. NId = <c>material|hệ|đơn vị đích</c>.</summary>
[Entity<MapMasterBlock>]
public sealed class UomConversion : Entity
{
    public string? MaterialNId { get; set; }

    public string? System { get; set; }

    public string? DestinationUom { get; set; }

    public decimal Factor { get; set; }
}

internal sealed record KanbanRow(string? Part, string? Dock, string? Supplier, string? Kanban, decimal Quantity, string? EffectiveFrom, string? EffectiveTo);

internal static class Kanbans
{
    private const string DateFormat = "yyyy-MM-dd";

    public static bool ValidDate(string? value) =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Còn hiệu lực ở <paramref name="day"/>. Ngày bắt đầu sai dạng là không hiệu lực.</summary>
    public static bool Active(string? from, string? to, DateOnly day)
    {
        if (!DateOnly.TryParseExact(from, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) || start > day)
        {
            return false;
        }

        return string.IsNullOrEmpty(to) || (DateOnly.TryParseExact(to, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) && end >= day);
    }

    public static MapResponse Upsert(IPlatform platform, KanbanRow row)
    {
        if (!ValidDate(row.EffectiveFrom) || (!string.IsNullOrEmpty(row.EffectiveTo) && !ValidDate(row.EffectiveTo)))
        {
            return MapResponse.Fail(NvErrors.InvalidKanban, $"Kanban {row.Kanban}: dates must be {DateFormat}.");
        }

        return LineCodeTables.Upsert<Kanban>(platform, row.Part + "|" + row.Dock + "|" + row.Supplier + "|" + row.EffectiveFrom, k =>
        {
            k.MaterialNId = row.Part;
            k.DockCode = row.Dock;
            k.SupplierCode = row.Supplier;
            k.KanbanNo = row.Kanban;
            k.QuantityPerContainer = row.Quantity;
            k.EffectiveFrom = row.EffectiveFrom;
            k.EffectiveTo = row.EffectiveTo;
        });
    }
}

/// <summary>Kanban lấy từ hệ ngoài: thêm hoặc sửa theo part, dock, nhà cung cấp, ngày hiệu lực. Một dòng sai ngày là bỏ cả lần nạp.</summary>
[Command<MapMasterBlock>]
public sealed class ImportKanbans : ICommand<MapResponse>
{
    [Mandatory]
    public string? Payload { get; init; }
}

internal sealed class ImportKanbansHandler : ICommandHandler<ImportKanbans, MapResponse>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public MapResponse Handle(ImportKanbans command, IPlatform platform)
    {
        List<KanbanRow>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<KanbanRow>>(command.Payload!, Json);
        }
        catch (JsonException e)
        {
            return MapResponse.Fail(NvErrors.InvalidKanban, "Kanban payload is not valid JSON: " + e.Message);
        }

        if (rows is null || rows.Count == 0)
        {
            return MapResponse.Fail(NvErrors.InvalidKanban, "Kanban payload has no rows.");
        }

        var last = new MapResponse();
        foreach (var row in rows)
        {
            last = Kanbans.Upsert(platform, row);
            if (!last.Succeeded)
            {
                return last;
            }
        }

        return last;
    }
}

[Command<MapMasterBlock>]
public sealed class SetKanban : ICommand<MapResponse>
{
    [Mandatory]
    public string? MaterialNId { get; init; }

    public string? DockCode { get; init; }

    public string? SupplierCode { get; init; }

    [Mandatory]
    public string? KanbanNo { get; init; }

    public decimal QuantityPerContainer { get; init; }

    [Mandatory]
    public string? EffectiveFrom { get; init; }

    public string? EffectiveTo { get; init; }
}

internal sealed class SetKanbanHandler : ICommandHandler<SetKanban, MapResponse>
{
    public MapResponse Handle(SetKanban command, IPlatform platform) =>
        Kanbans.Upsert(platform, new KanbanRow(command.MaterialNId, command.DockCode, command.SupplierCode, command.KanbanNo, command.QuantityPerContainer, command.EffectiveFrom, command.EffectiveTo));
}

[Command<MapMasterBlock>]
public sealed class SetUomConversion : ICommand<MapResponse>
{
    [Mandatory]
    public string? MaterialNId { get; init; }

    [Mandatory]
    public string? System { get; init; }

    [Mandatory]
    public string? DestinationUom { get; init; }

    public decimal Factor { get; init; }
}

internal sealed class SetUomConversionHandler : ICommandHandler<SetUomConversion, MapResponse>
{
    public MapResponse Handle(SetUomConversion command, IPlatform platform) =>
        LineCodeTables.Upsert<UomConversion>(platform, command.MaterialNId + "|" + command.System + "|" + command.DestinationUom, u =>
        {
            u.MaterialNId = command.MaterialNId;
            u.System = command.System;
            u.DestinationUom = command.DestinationUom;
            u.Factor = command.Factor;
        });
}

/// <summary>Màn part address: lưu rồi báo cổng nhà cung cấp, không chờ trả lời. Suite master data dùng <see cref="SetPartAddress"/>, không gửi tin.</summary>
[Command<MapMasterBlock>]
public sealed class SavePartAddress : ICommand<MapResponse>
{
    [Mandatory]
    public string? MaterialNId { get; init; }

    [Mandatory]
    public string? StoreAddress { get; init; }

    public string? KanbanNId { get; init; }

    public string? DeliveryAddress { get; init; }

    public string? DeliveryRouteNId { get; init; }

    public string? EffectiveFrom { get; init; }

    public string? EffectiveTo { get; init; }
}

internal sealed class SavePartAddressHandler : ICommandHandler<SavePartAddress, MapResponse>
{
    public MapResponse Handle(SavePartAddress command, IPlatform platform)
    {
        var nid = command.MaterialNId + "|" + command.StoreAddress;
        var action = platform.Query<PartAddress>().Any(a => a.NId == nid) ? "Update" : "Add";
        var saved = platform.CallCommand<SetPartAddress, MapResponse>(new SetPartAddress
        {
            MaterialNId = command.MaterialNId,
            StoreAddress = command.StoreAddress,
            KanbanNId = command.KanbanNId,
            DeliveryAddress = command.DeliveryAddress,
            DeliveryRouteNId = command.DeliveryRouteNId,
            EffectiveFrom = command.EffectiveFrom,
            EffectiveTo = command.EffectiveTo,
        });
        if (saved.Succeeded)
        {
            platform.SendMessageAsync(new OutboundMessage(MapAdapters.SupplierPortal, "PartAddress" + action, JsonSerializer.Serialize(new
            {
                part = command.MaterialNId,
                kanban = command.KanbanNId,
                store = command.StoreAddress,
                delivery = command.DeliveryAddress,
                route = command.DeliveryRouteNId,
            })));
        }

        return saved;
    }
}

[Command<MapMasterBlock>]
public sealed class DeletePartAddress : ICommand<MapResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class DeletePartAddressHandler : ICommandHandler<DeletePartAddress, MapResponse>
{
    public MapResponse Handle(DeletePartAddress command, IPlatform platform)
    {
        var address = platform.Query<PartAddress>().SingleOrDefault(a => a.NId == command.NId);
        if (address is null)
        {
            return MapResponse.Fail(NvErrors.UnknownPartAddress, $"Part address {command.NId} does not exist.");
        }

        platform.Delete(address);
        platform.SendMessageAsync(new OutboundMessage(MapAdapters.SupplierPortal, "PartAddressDelete", JsonSerializer.Serialize(new { part = address.MaterialNId, store = address.StoreAddress })));
        return new MapResponse { NId = command.NId };
    }
}

/// <summary>Adapter của bảng đối chiếu; nơi tin đi tới là cấu hình của tầng tích hợp.</summary>
public static class MapAdapters
{
    public const string SupplierPortal = "SupplierPortal";
}
