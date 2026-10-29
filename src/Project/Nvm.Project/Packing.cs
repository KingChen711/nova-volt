using System.Globalization;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Đóng gói thành phẩm, phần của team: bước đóng tote và xếp pallet chạy từ tin máy theo bảng cấu hình; post-action đếm lại
// tote, pallet sau mỗi lần nạp; dựng lại pallet sau bước đo cuối. Lớp Product chỉ gắn và gỡ vật chứa, không tự đếm.

public static class ContainerTypes
{
    public const string Tote = "Tote";
    public const string Pallet = "Pallet";
    public const string Ckd = "Ckd";
    public const string StorageTrailer = "StorageTrailer";
}

/// <summary>State machine của team cho vật chứa, nạp bằng dữ liệu (suite <c>NvProductionExt_009_ContainerStates</c>).</summary>
public static class NvStateMachines
{
    public const string Pallet = "PalletStateMachine";
    public const string Tote = "ToteStateMachine";
    public const string Ckd = "CkdStateMachine";
    public const string StorageTrailer = "StorageTrailerStateMachine";
}

public static class PalletStates
{
    public const string Empty = "Empty";
    public const string Full = "Full";
    public const string Picked = "Picked";
    public const string Staged = "Staged";
    public const string Loaded = "Loaded";
}

public static class ToteStates
{
    public const string New = "New";
    public const string Full = "Full";
    public const string Empty = "Empty";
}

/// <summary>Property của material mà phần thành phẩm đọc.</summary>
public static class NvMaterialProperties
{
    /// <summary>Số tote của một pallet đủ (số chuẩn của part). Cũng là sức chứa của gói lẻ.</summary>
    public const string PalletQuantity = "PalletQty";

    /// <summary><c>true</c>: part cần bước đo cuối trước khi cất kho.</summary>
    public const string EolvRequired = "EolvRequired";
}

/// <summary>Việc MOM làm thêm khi tin tới ở một bước đóng gói.</summary>
public static class PackingActions
{
    /// <summary>Sản phẩm của tin vào tote mang mã trong tin; tote chưa có thì tạo.</summary>
    public const string Tote = "Tote";

    /// <summary>Tin mang mã pallet (serial) và danh sách tote; pallet chưa có thì tạo, rồi nạp từng tote.</summary>
    public const string Pallet = "Pallet";

    /// <summary>Bước đo cuối: ghi bước đo cho sản phẩm, rồi gỡ tote chứa nó khỏi pallet.</summary>
    public const string Test = "Test";

    public static readonly IReadOnlyList<string> All = [Tote, Pallet, Test];
}

/// <summary>
/// Bước đóng gói: tin ở mã bước phía xưởng này (NId) thì MOM làm thêm <see cref="Action"/>. Bước không có trong bảng đối chiếu
/// bước (vd xếp pallet, đóng tote của họ B) chỉ làm việc đóng gói, không ghi WO.
/// </summary>
[Entity<NvMasterBlock>]
public sealed class PackingOperation : Entity
{
    /// <summary>Một trong <see cref="PackingActions"/>.</summary>
    [Mandatory]
    public string? Action { get; set; }

    /// <summary>Trạng thái của vật chứa mới tạo (tote: <c>New</c>; pallet: <c>Full</c>).</summary>
    public string? InitialStatus { get; set; }
}

[Command<NvMasterBlock>]
public sealed class SetPackingOperation : ICommand<NvResponse>
{
    [Mandatory]
    public string? OperationCode { get; init; }

    [Mandatory]
    public string? Action { get; init; }

    public string? InitialStatus { get; init; }
}

internal sealed class SetPackingOperationHandler : ICommandHandler<SetPackingOperation, NvResponse>
{
    public NvResponse Handle(SetPackingOperation command, IPlatform platform)
    {
        if (!PackingActions.All.Contains(command.Action))
        {
            return NvResponse.Fail(NvErrors.InvalidPackingAction, $"Packing action must be one of {string.Join(", ", PackingActions.All)}.");
        }

        var row = platform.Query<PackingOperation>().SingleOrDefault(p => p.NId == command.OperationCode) ?? platform.Create<PackingOperation>();
        row.NId = command.OperationCode!;
        row.Action = command.Action;
        row.InitialStatus = command.InitialStatus;
        platform.Submit(row);
        return NvResponse.Of(row);
    }
}

/// <summary>Thông tin team gắn lên vật chứa: ngày đóng pallet, giờ nạp vào trailer kho, cờ hết hạn bước đo cuối.</summary>
[Facet<NvOperationBlock>]
public sealed class PackingDetail : Facet<Mtua>
{
    /// <summary>Ghi mỗi lần nạp tote vào pallet; thứ tự đề xuất pallet khi lấy hàng đi theo ngày này, không theo ngày cất kho.</summary>
    public DateTimeOffset? BuildDate { get; set; }

    /// <summary>Giờ pallet được nạp vào trailer kho.</summary>
    public DateTimeOffset? LoadedOn { get; set; }

    /// <summary>Lần đo cuối của pallet đã quá số ngày cho phép (timer hằng ngày đặt).</summary>
    public bool EolvExpired { get; set; }
}

internal static class PackingData
{
    public static PackingDetail DetailOrNew(IPlatform platform, Mtua container)
    {
        var detail = platform.Query<PackingDetail>().SingleOrDefault(d => d.EntityId == container.Id);
        if (detail is null)
        {
            detail = platform.Create<PackingDetail>();
            detail.EntityId = container.Id;
        }

        return detail;
    }

    /// <summary>Số chuẩn của part (property <see cref="NvMaterialProperties.PalletQuantity"/>); thiếu hoặc sai dạng thì null.</summary>
    public static int? PalletQuantity(IReadOnlyPlatform platform, string? materialNId) =>
        int.TryParse(Property(platform, materialNId, NvMaterialProperties.PalletQuantity), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static bool EolvRequired(IReadOnlyPlatform platform, string? materialNId) =>
        string.Equals(Property(platform, materialNId, NvMaterialProperties.EolvRequired), "true", StringComparison.OrdinalIgnoreCase);

    private static string? Property(IReadOnlyPlatform platform, string? materialNId, string name)
    {
        var nid = materialNId + "|" + name;
        return platform.ProjectionQuery<MaterialProperty>().Where(p => p.NId == nid).Select(p => p.Value).SingleOrDefault();
    }

    /// <summary>MTU nằm trong vật chứa, kể cả trong các tầng con.</summary>
    public static List<Mtu> ItemsIn(IReadOnlyPlatform platform, string containerNId)
    {
        var containers = new List<string> { containerNId };
        var frontier = containers.ToList();
        while (frontier.Count > 0)
        {
            frontier = [.. platform.Query<Mtua>().Where(c => frontier.Contains(c.ParentMtuaNId!)).Select(c => c.NId)];
            containers.AddRange(frontier);
        }

        return [.. platform.Query<Mtu>().Where(m => containers.Contains(m.ParentMtuaNId!))];
    }

    /// <summary>
    /// Sản phẩm có tin của bước đo cuối với kết quả OK. Hàng không ra từ chuyền (<c>Code</c> rỗng hoặc bằng NId, vd tạo bằng
    /// suite) không bị đòi bước đo, như cách code của team nhận ra hàng thật.
    /// </summary>
    public static bool EolvPassed(IReadOnlyPlatform platform, Mtu item)
    {
        if (string.IsNullOrEmpty(item.Code) || item.Code == item.NId)
        {
            return true;
        }

        var tests = platform.ProjectionQuery<PackingOperation>().Where(p => p.Action == PackingActions.Test).Select(p => p.NId).ToList();
        return platform.Query<LineExecution>().Any(e => e.Serial == item.Code && tests.Contains(e.OperationCode!) && e.StatusId == "OK");
    }

    /// <summary>
    /// Đếm lại vật chứa sau khi gỡ hàng: số lượng = số con trực tiếp, hết con thì bỏ material và về trạng thái rỗng của nó. Code
    /// của team gọi sau mỗi lần tự gỡ; lệnh gỡ của lớp Product không đếm.
    /// </summary>
    public static ProductResponse Recount(IPlatform platform, string containerNId)
    {
        var container = platform.Query<Mtua>().Single(c => c.NId == containerNId);
        var count = platform.Query<Mtua>().Count(c => c.ParentMtuaNId == containerNId) + platform.Query<Mtu>().Count(m => m.ParentMtuaNId == containerNId);
        var updated = platform.CallCommand<UpdateMtua, ProductResponse>(new UpdateMtua { NId = containerNId, Quantity = count, ClearMaterial = count == 0 });
        if (!updated.Succeeded || count > 0 || EmptyStatus(container.ContainerType) is not { } empty || container.Status == empty)
        {
            return updated;
        }

        return platform.CallCommand<SetMtuaStatusForced, ProductResponse>(new SetMtuaStatusForced { NId = containerNId, Status = empty });
    }

    private static string? EmptyStatus(string? containerType) => containerType switch
    {
        ContainerTypes.Pallet => PalletStates.Empty,
        ContainerTypes.Tote => ToteStates.Empty,
        ContainerTypes.Ckd => PalletStates.Empty,
        _ => null,
    };
}

/// <summary>
/// Post-action của team trên lệnh gắn vào vật chứa: số lượng của tote, pallet, gói lẻ = số con trực tiếp; vật chứa chưa có
/// material thì nhận material của con; pallet ghi ngày đóng. Gắn một MTU thẳng vào pallet đang chứa tote thì tháo hết tote ra
/// trước khi đếm (lớp Product chỉ gắn).
/// </summary>
[PostAction<NvOperationBlock>]
public sealed class UpdatePalletBuild : IPostAction<AssembleIntoMtua, ProductResponse>
{
    public ProductResponse Execute(AssembleIntoMtua command, ProductResponse response, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(platform);
        var container = platform.Query<Mtua>().Single(c => c.NId == command.ContainerNId);
        if (container.ContainerType is not (ContainerTypes.Tote or ContainerTypes.Pallet or ContainerTypes.Ckd))
        {
            return response;
        }

        var mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == command.ChildNId);
        if (mtu is not null && container.ContainerType == ContainerTypes.Pallet)
        {
            foreach (var tote in platform.Query<Mtua>().Where(c => c.ParentMtuaNId == container.NId).Select(c => c.NId).ToList())
            {
                platform.Tracer.Write(TraceLevel.Information, $"{mtu.NId} loaded straight onto {container.NId}: tote {tote} taken off.");
                var removed = platform.CallCommand<RemoveFromMtua, ProductResponse>(new RemoveFromMtua { ChildNId = tote });
                if (!removed.Succeeded)
                {
                    return removed;
                }
            }
        }

        var material = container.MaterialNId ?? mtu?.MaterialNId
            ?? platform.Query<Mtua>().Where(c => c.NId == command.ChildNId).Select(c => c.MaterialNId).SingleOrDefault();
        var count = platform.Query<Mtua>().Count(c => c.ParentMtuaNId == container.NId) + platform.Query<Mtu>().Count(m => m.ParentMtuaNId == container.NId);
        var updated = platform.CallCommand<UpdateMtua, ProductResponse>(new UpdateMtua { NId = container.NId, Quantity = count, MaterialNId = material });
        if (!updated.Succeeded)
        {
            return updated;
        }

        if (container.ContainerType == ContainerTypes.Pallet)
        {
            var detail = PackingData.DetailOrNew(platform, container);
            detail.BuildDate = platform.Time.GetUtcNow();
            platform.Submit(detail);
        }

        return response;
    }
}

/// <summary>Kết quả đóng gói của một tin; null là xong.</summary>
internal static class LinePacking
{
    /// <summary>Việc đóng gói sau khi tin đã ghi vào WO (hoặc thay cho việc ghi WO, với bước chỉ đóng gói).</summary>
    public static LineError? Apply(IPlatform platform, PackingOperation config, LineOperationDetail detail, string? productNId, string equipmentNId) =>
        config.Action switch
        {
            PackingActions.Tote => IntoTote(platform, config, detail, productNId, equipmentNId),
            PackingActions.Pallet => OntoPallet(platform, config, detail, equipmentNId),
            PackingActions.Test => OffPallet(platform, detail, productNId, equipmentNId),
            _ => new LineError(LineErrors.PackingFailed, $"Packing action {config.Action} is not supported."),
        };

    /// <summary>Sản phẩm của bước chỉ đóng gói: MTU còn hàng mang serial trong tin, mới nhất trước.</summary>
    public static string? ProductBySerial(IPlatform platform, string serial) =>
        platform.Query<Mtu>().Where(m => m.Code == serial && m.Quantity > 0 && m.Kind == "Serial")
            .OrderByDescending(m => m.CreatedOn).Select(m => m.NId).FirstOrDefault();

    private static LineError? IntoTote(IPlatform platform, PackingOperation config, LineOperationDetail detail, string? productNId, string equipmentNId)
    {
        if (string.IsNullOrEmpty(detail.ContainerCode))
        {
            return new LineError(LineErrors.PackingFailed, $"Message for {detail.Serial} at {detail.OperationCode} has no tote code.");
        }

        if (productNId is null)
        {
            return new LineError(LineErrors.UnknownMtu, $"No MTU with serial {detail.Serial} to put into tote {detail.ContainerCode}.");
        }

        if (Ensure(platform, detail.ContainerCode, ContainerTypes.Tote, NvStateMachines.Tote, config.InitialStatus ?? ToteStates.New, equipmentNId) is { } failed)
        {
            return failed;
        }

        var packed = platform.CallCommand<AssembleIntoMtua, ProductResponse>(new AssembleIntoMtua { ChildNId = productNId, ContainerNId = detail.ContainerCode });
        return packed.Succeeded ? null : new LineError(LineErrors.PackingFailed, packed.Error!.ErrorMessage);
    }

    private static LineError? OntoPallet(IPlatform platform, PackingOperation config, LineOperationDetail detail, string equipmentNId)
    {
        var totes = detail.Contents ?? [];
        if (totes.Count == 0)
        {
            return new LineError(LineErrors.PackingFailed, $"Pallet message {detail.Serial} lists no tote.");
        }

        // Kiểm hết tote trước khi ghi: tote chưa tới (tin đóng tote đến sau) thì cả tin chờ gửi lại, không để pallet dở.
        var known = platform.Query<Mtua>().Where(c => totes.Contains(c.NId) && c.ContainerType == ContainerTypes.Tote).Select(c => c.NId).ToList();
        if (totes.FirstOrDefault(t => !known.Contains(t)) is { } missing)
        {
            return new LineError(LineErrors.PackingFailed, $"Tote {missing} of pallet {detail.Serial} is not known yet.");
        }

        if (Ensure(platform, detail.Serial!, ContainerTypes.Pallet, NvStateMachines.Pallet, config.InitialStatus ?? PalletStates.Full, equipmentNId) is { } failed)
        {
            return failed;
        }

        foreach (var tote in totes)
        {
            var packed = platform.CallCommand<AssembleIntoMtua, ProductResponse>(new AssembleIntoMtua { ChildNId = tote, ContainerNId = detail.Serial });
            if (!packed.Succeeded)
            {
                return new LineError(LineErrors.PackingFailed, packed.Error!.ErrorMessage);
            }
        }

        return null;
    }

    /// <summary>Bước đo cuối: tote chứa sản phẩm rời pallet, pallet đếm lại (hết tote thì về Empty, giữ vị trí).</summary>
    private static LineError? OffPallet(IPlatform platform, LineOperationDetail detail, string? productNId, string equipmentNId)
    {
        var tote = platform.Query<Mtu>().Where(m => m.NId == productNId).Select(m => m.ParentMtuaNId).SingleOrDefault();
        var pallet = tote is null ? null : platform.Query<Mtua>().Where(c => c.NId == tote).Select(c => c.ParentMtuaNId).SingleOrDefault();
        if (pallet is null)
        {
            platform.Tracer.Write(TraceLevel.Verbose, $"{productNId} is not on a pallet; test recorded only.");
            return null;
        }

        var removed = platform.CallCommand<RemoveFromMtua, ProductResponse>(new RemoveFromMtua { ChildNId = tote, EquipmentNId = equipmentNId });
        if (!removed.Succeeded)
        {
            return new LineError(LineErrors.PackingFailed, removed.Error!.ErrorMessage);
        }

        var recount = PackingData.Recount(platform, pallet);
        platform.Tracer.Write(TraceLevel.Information, $"Tested {detail.Serial}: tote {tote} taken off pallet {pallet}.");
        return recount.Succeeded ? null : new LineError(LineErrors.PackingFailed, recount.Error!.ErrorMessage);
    }

    private static LineError? Ensure(IPlatform platform, string nid, string type, string machine, string status, string equipmentNId)
    {
        if (platform.Query<Mtua>().Any(c => c.NId == nid))
        {
            return null;
        }

        var created = platform.CallCommand<CreateMtua, ProductResponse>(
            new CreateMtua { NId = nid, ContainerType = type, EquipmentNId = equipmentNId, StateMachine = machine, Status = status });
        return created.Succeeded ? null : new LineError(LineErrors.PackingFailed, created.Error!.ErrorMessage);
    }
}

/// <summary>
/// Dựng lại pallet sau bước đo cuối (màn của xưởng): mã chưa có thì tạo pallet rỗng; mã đã có thì phải đang rỗng hoặc đang
/// dựng dở. Pallet không mang vị trí.
/// </summary>
[Command<NvOperationBlock>]
public sealed class StartPalletRebuild : ICommand<NvResponse>
{
    [Mandatory]
    public string? PalletNId { get; init; }
}

internal sealed class StartPalletRebuildHandler : ICommandHandler<StartPalletRebuild, NvResponse>
{
    public NvResponse Handle(StartPalletRebuild command, IPlatform platform)
    {
        var pallet = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.PalletNId);
        if (pallet is not null)
        {
            return pallet.ContainerType == ContainerTypes.Pallet && pallet.Status == PalletStates.Empty
                ? NvResponse.Of(pallet)
                : NvResponse.Fail(NvErrors.InvalidPallet, $"{pallet.NId} is not an empty pallet ({pallet.ContainerType}, {pallet.Status}).");
        }

        var created = platform.CallCommand<CreateMtua, ProductResponse>(new CreateMtua
        {
            NId = command.PalletNId,
            ContainerType = ContainerTypes.Pallet,
            StateMachine = NvStateMachines.Pallet,
            Status = PalletStates.Empty,
        });
        return created.Succeeded ? new NvResponse { Id = created.Id, NId = created.NId } : NvResponse.Fail(created.Error!.ErrorCode, created.Error.ErrorMessage);
    }
}

/// <summary>
/// Nạp một tote vào pallet đang dựng lại. Mọi sản phẩm trong tote phải đã qua bước đo cuối, không bị giữ hay hủy, cùng material
/// với pallet. Tote đầu đặt material cho pallet; số tote tối đa là số chuẩn của part.
/// </summary>
[Command<NvOperationBlock>]
public sealed class AddToteToRebuiltPallet : ICommand<RebuildResponse>
{
    [Mandatory]
    public string? PalletNId { get; init; }

    [Mandatory]
    public string? ToteNId { get; init; }
}

public sealed class RebuildResponse : CommandResponse
{
    public int Loaded { get; set; }

    public int Capacity { get; set; }

    internal static RebuildResponse Fail(int code, string message)
    {
        var response = new RebuildResponse();
        response.SetError(code, message);
        return response;
    }
}

internal sealed class AddToteToRebuiltPalletHandler : ICommandHandler<AddToteToRebuiltPallet, RebuildResponse>
{
    public RebuildResponse Handle(AddToteToRebuiltPallet command, IPlatform platform)
    {
        var pallet = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.PalletNId && c.ContainerType == ContainerTypes.Pallet);
        if (pallet is null || pallet.Status != PalletStates.Empty)
        {
            return RebuildResponse.Fail(NvErrors.InvalidPallet, $"Pallet {command.PalletNId} is not being rebuilt.");
        }

        var tote = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.ToteNId && c.ContainerType == ContainerTypes.Tote);
        if (tote is null)
        {
            return RebuildResponse.Fail(NvErrors.UnknownContainer, $"Tote {command.ToteNId} does not exist.");
        }

        if (tote.ParentMtuaNId == pallet.NId)
        {
            return RebuildResponse.Fail(NvErrors.InvalidPallet, $"Tote {tote.NId} is already on pallet {pallet.NId}.");
        }

        var items = platform.Query<Mtu>().Where(m => m.ParentMtuaNId == tote.NId).ToList();
        if (items.Count == 0)
        {
            return RebuildResponse.Fail(NvErrors.InvalidPallet, $"Tote {tote.NId} is empty.");
        }

        if (items.FirstOrDefault(m => !PackingData.EolvPassed(platform, m)) is { } untested)
        {
            return RebuildResponse.Fail(NvErrors.NotTested, $"{untested.NId} in tote {tote.NId} has no passing end-of-line test.");
        }

        if (items.FirstOrDefault(m => m.Status != MtuStatus.Available) is { } held)
        {
            return RebuildResponse.Fail(NvErrors.HeldOrScrapped, $"{held.NId} in tote {tote.NId} is {held.Status}.");
        }

        var material = pallet.MaterialNId ?? items[0].MaterialNId;
        if (items.FirstOrDefault(m => m.MaterialNId != material) is { } other)
        {
            return RebuildResponse.Fail(NvErrors.MaterialMismatch, $"{other.NId} is {other.MaterialNId}; pallet {pallet.NId} holds {material}.");
        }

        if (PackingData.PalletQuantity(platform, material) is not { } capacity)
        {
            return RebuildResponse.Fail(NvErrors.MissingPalletQuantity, $"Material {material} has no {NvMaterialProperties.PalletQuantity} property.");
        }

        if (pallet.Quantity >= capacity)
        {
            return RebuildResponse.Fail(NvErrors.PalletFull, $"Pallet {pallet.NId} already holds {capacity} tote(s).");
        }

        if (tote.ParentMtuaNId is { } previous)
        {
            var off = platform.CallCommand<RemoveFromMtua, ProductResponse>(new RemoveFromMtua { ChildNId = tote.NId });
            var recount = off.Succeeded ? PackingData.Recount(platform, previous) : off;
            if (!recount.Succeeded)
            {
                return RebuildResponse.Fail(recount.Error!.ErrorCode, recount.Error.ErrorMessage);
            }
        }

        var loaded = platform.CallCommand<AssembleIntoMtua, ProductResponse>(new AssembleIntoMtua { ChildNId = tote.NId, ContainerNId = pallet.NId });
        if (!loaded.Succeeded)
        {
            return RebuildResponse.Fail(loaded.Error!.ErrorCode, loaded.Error.ErrorMessage);
        }

        return new RebuildResponse { Loaded = platform.Query<Mtua>().Count(c => c.ParentMtuaNId == pallet.NId), Capacity = capacity };
    }
}

/// <summary>Chốt pallet dựng lại: đủ số tote chuẩn thì sang Full.</summary>
[Command<NvOperationBlock>]
public sealed class CompletePalletRebuild : ICommand<NvResponse>
{
    [Mandatory]
    public string? PalletNId { get; init; }
}

internal sealed class CompletePalletRebuildHandler : ICommandHandler<CompletePalletRebuild, NvResponse>
{
    public NvResponse Handle(CompletePalletRebuild command, IPlatform platform)
    {
        var pallet = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.PalletNId && c.ContainerType == ContainerTypes.Pallet);
        if (pallet is null || pallet.Status != PalletStates.Empty)
        {
            return NvResponse.Fail(NvErrors.InvalidPallet, $"Pallet {command.PalletNId} is not being rebuilt.");
        }

        var capacity = PackingData.PalletQuantity(platform, pallet.MaterialNId);
        if (capacity is null || pallet.Quantity != capacity)
        {
            return NvResponse.Fail(NvErrors.PalletNotComplete, $"Pallet {pallet.NId} holds {pallet.Quantity:0} of {capacity?.ToString(CultureInfo.InvariantCulture) ?? "?"} tote(s).");
        }

        var full = platform.CallCommand<SetMtuaStatus, ProductResponse>(new SetMtuaStatus { NId = pallet.NId, Status = PalletStates.Full });
        return full.Succeeded ? NvResponse.Of(pallet) : NvResponse.Fail(full.Error!.ErrorCode, full.Error.ErrorMessage);
    }
}
