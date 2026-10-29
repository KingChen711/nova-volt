using System.Globalization;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Thành phẩm, phần của team: app riêng ShipApp và FB của nó. Cất pallet vào ô kho theo địa chỉ của part, bản ghi FIFO (post-action
// trên lệnh chuyển vật chứa của lớp Product phát event, subscriber ghi), kiểm pallet bằng procedure, gỡ hàng khỏi pallet, gói
// lẻ, hết hạn bước đo cuối. Xuất hàng (đơn, picking, staging) thuộc R8.

[FunctionalBlock("FB_OP_Ship", ModelDomain.OperationalData)]
public sealed class ShipOperationBlock;

/// <summary>App thành phẩm của team: màn cất kho, kiểm pallet, gỡ pallet, gói lẻ. App độc lập, không gắn vào Production.</summary>
[App(Name)]
public sealed class ShipApp : AppDefinition
{
    public const string Name = "ShipApp";

    protected override void Define(PomBuilder pom) => pom
        .Entity<FifoPallet>().Entity<Mtua>().Entity<Mtu>().Entity<PackingDetail>().Entity<PartAddress>()
        .Command<StorePallet>().Command<PalletStatusValidation>().Command<UnloadTotesFromPallet>()
        .Command<OpenCkdPackage>().Command<AddItemToCkdPackage>().Command<RemoveItemFromCkdPackage>().Command<CheckEolvRetest>()
        .ReadingFunction<PalletHeldItems>().ReadingFunction<PalletDataCheck>().ReadingFunction<SuggestedPallets>()
        .ReadingFunction<StoreAddresses>();
}

/// <summary>
/// Bản ghi FIFO của một pallet: pallet nào sẵn sàng lấy, ở đâu, ngày đóng pallet. NId = mã pallet. Ghi sau khi lệnh chuyển
/// pallet commit, bởi subscriber của event <see cref="PalletLocated"/>.
/// </summary>
[Entity<ShipOperationBlock>]
public sealed class FifoPallet : Entity
{
    public string? MaterialNId { get; set; }

    public string? LocationNId { get; set; }

    /// <summary>Trạng thái pallet lúc ghi.</summary>
    public string? Status { get; set; }

    public DateTimeOffset? BuildDate { get; set; }

    public DateTimeOffset? LocatedOn { get; set; }
}

/// <summary>Pallet vừa được đặt ở một vị trí (ô kho, cửa dock, trailer kho). Phát sau commit.</summary>
[Event<ShipOperationBlock>]
[EventVersion(1)]
public sealed record PalletLocated(string PalletNId, string? LocationNId) : IFoundationEvent;

public sealed class ShipResponse : CommandResponse
{
    public string? NId { get; set; }

    public string? Status { get; set; }

    public decimal? Quantity { get; set; }

    internal static ShipResponse Fail(int code, string message)
    {
        var response = new ShipResponse();
        response.SetError(code, message);
        return response;
    }

    internal static ShipResponse From(ProductResponse failed) => Fail(failed.Error!.ErrorCode, failed.Error.ErrorMessage);
}

/// <summary>
/// Cất pallet vào một ô kho (màn Storage: quét pallet rồi quét ô). Ô phải là store address của part trong bảng địa chỉ; pallet phải
/// Full. Việc ghi FIFO và kiểm bước đo cuối nằm ở post-action của lệnh chuyển vật chứa.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class StorePallet : ICommand<ShipResponse>
{
    [Mandatory]
    public string? PalletNId { get; init; }

    [Mandatory]
    public string? LocationNId { get; init; }
}

internal sealed class StorePalletHandler : ICommandHandler<StorePallet, ShipResponse>
{
    public ShipResponse Handle(StorePallet command, IPlatform platform)
    {
        var pallet = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.PalletNId && c.ContainerType == ContainerTypes.Pallet);
        if (pallet is null)
        {
            return ShipResponse.Fail(NvErrors.InvalidPallet, $"Pallet {command.PalletNId} does not exist.");
        }

        if (pallet.Status != PalletStates.Full)
        {
            return ShipResponse.Fail(NvErrors.InvalidPallet, $"Pallet {pallet.NId} is {pallet.Status}; only a full pallet is stored.");
        }

        var addresses = platform.ProjectionQuery<PartAddress>().Where(a => a.MaterialNId == pallet.MaterialNId).Select(a => a.StoreAddress).ToList();
        if (addresses.Count == 0)
        {
            return ShipResponse.Fail(NvErrors.NoStoreAddress, $"Material {pallet.MaterialNId} has no store address in the part address table.");
        }

        if (!addresses.Contains(command.LocationNId))
        {
            return ShipResponse.Fail(NvErrors.NotAStoreAddress,
                $"{command.LocationNId} is not a store address of {pallet.MaterialNId} ({string.Join(", ", addresses)}).");
        }

        var moved = platform.CallCommand<MoveMtua, ProductResponse>(new MoveMtua { NId = pallet.NId, EquipmentNId = command.LocationNId });
        return moved.Succeeded ? new ShipResponse { NId = pallet.NId, Status = pallet.Status, Quantity = pallet.Quantity } : ShipResponse.From(moved);
    }
}

/// <summary>
/// Post-action của team trên lệnh chuyển vật chứa của lớp Product, chạy ở mọi app: với pallet của part cần bước đo cuối, chặn
/// nếu còn hàng ra từ chuyền chưa đo (key <see cref="NvKeys.EolvValidation"/> = <c>false</c> thì bỏ kiểm); rồi hẹn phát
/// <see cref="PalletLocated"/> sau commit. Post-action không tự ghi FIFO.
/// </summary>
[PostAction<ShipOperationBlock>]
public sealed class PalletFifoOnMove : IPostAction<MoveMtua, ProductResponse>
{
    public ProductResponse Execute(MoveMtua command, ProductResponse response, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(platform);
        var pallet = platform.Query<Mtua>().Single(c => c.NId == command.NId);
        if (pallet.ContainerType != ContainerTypes.Pallet)
        {
            return response;
        }

        if (FinishedGoods.UntestedItem(platform, pallet) is { } untested)
        {
            var failed = new ProductResponse();
            failed.SetError(NvErrors.NotTested, $"Pallet {pallet.NId}: {untested} has no passing end-of-line test.");
            return failed;
        }

        platform.RaiseEventOnCommit(new PalletLocated(pallet.NId, command.EquipmentNId));
        return response;
    }
}

/// <summary>Ghi bản ghi FIFO của pallet vừa được đặt chỗ: vị trí, trạng thái, material, ngày đóng pallet.</summary>
[EventSubscriber<ShipOperationBlock>(Subscription = "PalletFifo", Predefined = true, WorkerRole = "FinishedGoods")]
public sealed class PalletFifoWriter : IEventSubscriber<PalletLocated>
{
    public void Handle(PalletLocated domainEvent, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(platform);
        var pallet = platform.Query<Mtua>().SingleOrDefault(c => c.NId == domainEvent.PalletNId);
        if (pallet is null)
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Pallet {domainEvent.PalletNId} is gone; no FIFO record written.");
            return;
        }

        var record = platform.Query<FifoPallet>().SingleOrDefault(f => f.NId == pallet.NId) ?? platform.Create<FifoPallet>();
        record.NId = pallet.NId;
        record.MaterialNId = pallet.MaterialNId;
        record.LocationNId = MtuLocations.EquipmentOf(platform.Query<Mtu>(), platform.Query<Mtua>(), pallet.NId);
        record.Status = pallet.Status;
        record.BuildDate = platform.Query<PackingDetail>().Where(d => d.EntityId == pallet.Id).Select(d => d.BuildDate).SingleOrDefault();
        record.LocatedOn = platform.Time.GetUtcNow();
        platform.Submit(record);
        platform.Tracer.Write(TraceLevel.Information, $"FIFO record of {pallet.NId}: {record.LocationNId}, built {record.BuildDate:O}.");
    }
}

internal static class FinishedGoods
{
    /// <summary>Hàng chưa qua bước đo cuối của pallet; null khi part không cần đo hoặc key tắt kiểm.</summary>
    public static string? UntestedItem(IReadOnlyPlatform platform, Mtua pallet)
    {
        if (!PackingData.EolvRequired(platform, pallet.MaterialNId))
        {
            return null;
        }

        var key = platform.ProjectionQuery<ConfigurationKey>().Where(k => k.NId == NvKeys.EolvValidation).Select(k => k.Val).SingleOrDefault();
        if (string.Equals(key, "false", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return PackingData.ItemsIn(platform, pallet.NId).Where(m => !PackingData.EolvPassed(platform, m)).Select(m => m.NId).FirstOrDefault();
    }

    /// <summary>Pallet có hàng ra từ chuyền và mọi hàng đó đã đo.</summary>
    public static bool Tested(IReadOnlyPlatform platform, Mtua pallet) =>
        PackingData.ItemsIn(platform, pallet.NId).All(m => PackingData.EolvPassed(platform, m));
}

public sealed class PalletParameters
{
    public string? PalletNId { get; init; }
}

/// <summary>
/// Kiểm hàng lỗi của pallet (nút QA Check; cùng phép kiểm lúc lấy hàng): MTU bị giữ hoặc hủy ở bất kỳ tầng nào của cây thành phần
/// thì pallet không lấy được.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class PalletStatusValidation : ICommand<ShipResponse>
{
    [Mandatory]
    public string? PalletNId { get; init; }
}

internal sealed class PalletStatusValidationHandler : ICommandHandler<PalletStatusValidation, ShipResponse>
{
    public ShipResponse Handle(PalletStatusValidation command, IPlatform platform)
    {
        var held = FinishedGoodsSql.Held(FinishedGoodsSql.Connection(platform.ProjectionQuery<ConfigurationKey>), command.PalletNId!);
        if (held.Count > 0)
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Pallet {command.PalletNId}: {string.Join(", ", held.Select(h => $"{h.MtuNId} {h.Status}"))}.");
            return ShipResponse.Fail(NvErrors.HeldOrScrapped,
                $"Pallet {command.PalletNId} holds {held.Count} item(s) on hold or scrapped and cannot be picked.");
        }

        return new ShipResponse { NId = command.PalletNId };
    }
}

/// <summary>Màn kiểm thành phần: MTU bị giữ hoặc hủy trong cây của pallet, kèm NC mở. Danh sách trống là pallet sạch.</summary>
[ReadingFunction(ShipApp.Name)]
public sealed class PalletHeldItems : IReadingFunction<PalletParameters, PalletHeldItem>
{
    public IQueryable<PalletHeldItem> Read(PalletParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        return FinishedGoodsSql.Held(FinishedGoodsSql.Connection(platform.ProjectionQuery<ConfigurationKey>), parameters.PalletNId ?? string.Empty)
            .AsQueryable();
    }
}

public sealed class PalletDataParameters
{
    public string? PalletNId { get; init; }

    public string? MaterialNId { get; init; }
}

/// <summary>Màn số liệu pallet: năm điều kiểm, theo mã pallet hoặc theo part.</summary>
[ReadingFunction(ShipApp.Name)]
public sealed class PalletDataCheck : IReadingFunction<PalletDataParameters, PalletDataRow>
{
    public IQueryable<PalletDataRow> Read(PalletDataParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        return FinishedGoodsSql.Data(FinishedGoodsSql.Connection(platform.ProjectionQuery<ConfigurationKey>), parameters.PalletNId, parameters.MaterialNId)
            .AsQueryable();
    }
}

public sealed class MaterialParameters
{
    public string? MaterialNId { get; init; }
}

/// <summary>Pallet đề xuất lấy cho một part theo FIFO (bản ghi FIFO, ngày đóng pallet).</summary>
[ReadingFunction(ShipApp.Name)]
public sealed class SuggestedPallets : IReadingFunction<MaterialParameters, SuggestedPalletRow>
{
    public IQueryable<SuggestedPalletRow> Read(MaterialParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        return FinishedGoodsSql.Suggested(FinishedGoodsSql.Connection(platform.ProjectionQuery<ConfigurationKey>), parameters.MaterialNId ?? string.Empty)
            .AsQueryable();
    }
}

/// <summary>Ô kho gợi ý cho màn cất kho: store address của part.</summary>
[ReadingFunction(ShipApp.Name)]
public sealed class StoreAddresses : IReadingFunction<MaterialParameters, PartAddress>
{
    public IQueryable<PartAddress> Read(MaterialParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        return platform.ProjectionQuery<PartAddress>().Where(a => a.MaterialNId == parameters.MaterialNId);
    }
}

/// <summary>
/// Gỡ mọi tote khỏi pallet (màn gỡ pallet): tote về <c>n/a</c>, pallet số lượng 0, mất material, ép Empty. Không kiểm NC, không
/// đổi vị trí của pallet.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class UnloadTotesFromPallet : ICommand<ShipResponse>
{
    [Mandatory]
    public string? PalletNId { get; init; }
}

internal sealed class UnloadTotesFromPalletHandler : ICommandHandler<UnloadTotesFromPallet, ShipResponse>
{
    public ShipResponse Handle(UnloadTotesFromPallet command, IPlatform platform)
    {
        var pallet = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.PalletNId && c.ContainerType == ContainerTypes.Pallet);
        if (pallet is null)
        {
            return ShipResponse.Fail(NvErrors.InvalidPallet, $"Pallet {command.PalletNId} does not exist.");
        }

        var children = platform.Query<Mtua>().Where(c => c.ParentMtuaNId == pallet.NId).Select(c => c.NId).ToList();
        children.AddRange(platform.Query<Mtu>().Where(m => m.ParentMtuaNId == pallet.NId).Select(m => m.NId));
        foreach (var child in children)
        {
            var removed = platform.CallCommand<RemoveFromMtua, ProductResponse>(new RemoveFromMtua { ChildNId = child });
            if (!removed.Succeeded)
            {
                return ShipResponse.From(removed);
            }
        }

        var recount = PackingData.Recount(platform, pallet.NId);
        if (!recount.Succeeded)
        {
            return ShipResponse.From(recount);
        }

        var empty = platform.CallCommand<SetMtuaStatusForced, ProductResponse>(new SetMtuaStatusForced { NId = pallet.NId, Status = PalletStates.Empty });
        return empty.Succeeded ? new ShipResponse { NId = pallet.NId, Status = PalletStates.Empty, Quantity = 0m } : ShipResponse.From(empty);
    }
}

/// <summary>
/// Mở gói lẻ theo mã (gói chưa có thì tạo, Empty). Có key <see cref="NvKeys.CkdPrefix"/> thì mã phải bắt đầu bằng tiền tố, dài
/// đúng <see cref="NvKeys.CkdLength"/>, phần sau tiền tố toàn chữ số.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class OpenCkdPackage : ICommand<ShipResponse>
{
    [Mandatory]
    public string? PackageNId { get; init; }
}

internal sealed class OpenCkdPackageHandler : ICommandHandler<OpenCkdPackage, ShipResponse>
{
    public ShipResponse Handle(OpenCkdPackage command, IPlatform platform)
    {
        var code = command.PackageNId!;
        var keys = platform.ProjectionQuery<ConfigurationKey>().Where(k => k.NId == NvKeys.CkdPrefix || k.NId == NvKeys.CkdLength)
            .ToDictionary(k => k.NId, k => k.Val);
        if (keys.GetValueOrDefault(NvKeys.CkdPrefix) is { Length: > 0 } prefix)
        {
            var length = int.TryParse(keys.GetValueOrDefault(NvKeys.CkdLength), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : (int?)null;
            if (!code.StartsWith(prefix, StringComparison.Ordinal) || (length is not null && code.Length != length)
                || !code[prefix.Length..].All(char.IsAsciiDigit) || code.Length == prefix.Length)
            {
                return ShipResponse.Fail(NvErrors.InvalidPackageCode,
                    $"{code} is not a package code: expected {prefix} followed by digits, {length?.ToString(CultureInfo.InvariantCulture) ?? "any"} characters in all.");
            }
        }

        var package = platform.Query<Mtua>().SingleOrDefault(c => c.NId == code);
        if (package is not null)
        {
            return package.ContainerType == ContainerTypes.Ckd
                ? new ShipResponse { NId = package.NId, Status = package.Status, Quantity = package.Quantity }
                : ShipResponse.Fail(NvErrors.InvalidPackageCode, $"{code} is a {package.ContainerType}, not a package.");
        }

        var created = platform.CallCommand<CreateMtua, ProductResponse>(
            new CreateMtua { NId = code, ContainerType = ContainerTypes.Ckd, StateMachine = NvStateMachines.Ckd, Status = PalletStates.Empty });
        return created.Succeeded ? new ShipResponse { NId = code, Status = PalletStates.Empty, Quantity = 0m } : ShipResponse.From(created);
    }
}

/// <summary>
/// Đóng một sản phẩm thẳng vào gói lẻ (không qua tote). Sản phẩm phải Available, chưa ở gói nào, cùng material với gói, gói chưa
/// đủ số chuẩn. Sản phẩm đang trong tote thì tote rời pallet. Gói thành Full ngay khi có hàng.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class AddItemToCkdPackage : ICommand<ShipResponse>
{
    [Mandatory]
    public string? PackageNId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class AddItemToCkdPackageHandler : ICommandHandler<AddItemToCkdPackage, ShipResponse>
{
    public ShipResponse Handle(AddItemToCkdPackage command, IPlatform platform)
    {
        var package = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.PackageNId && c.ContainerType == ContainerTypes.Ckd);
        if (package is null)
        {
            return ShipResponse.Fail(NvErrors.UnknownContainer, $"Package {command.PackageNId} is not open.");
        }

        var item = platform.Query<Mtu>().SingleOrDefault(m => m.NId == command.MtuNId);
        if (item is null)
        {
            return ShipResponse.Fail(NvErrors.UnknownMtu, $"MTU {command.MtuNId} does not exist.");
        }

        if (item.Status != MtuStatus.Available)
        {
            return ShipResponse.Fail(NvErrors.HeldOrScrapped, $"{item.NId} is {item.Status}.");
        }

        var parent = item.ParentMtuaNId is null ? null : platform.Query<Mtua>().Single(c => c.NId == item.ParentMtuaNId);
        if (parent?.ContainerType == ContainerTypes.Ckd)
        {
            return ShipResponse.Fail(NvErrors.AlreadyPacked, $"{item.NId} is already in package {parent.NId}.");
        }

        if (package.MaterialNId is not null && package.MaterialNId != item.MaterialNId)
        {
            return ShipResponse.Fail(NvErrors.MaterialMismatch, $"{item.NId} is {item.MaterialNId}; package {package.NId} holds {package.MaterialNId}.");
        }

        var capacity = PackingData.PalletQuantity(platform, item.MaterialNId);
        if (capacity is null)
        {
            return ShipResponse.Fail(NvErrors.MissingPalletQuantity, $"Material {item.MaterialNId} has no {NvMaterialProperties.PalletQuantity} property.");
        }

        if (package.Quantity >= capacity)
        {
            return ShipResponse.Fail(NvErrors.PalletFull, $"Package {package.NId} already holds {capacity} item(s).");
        }

        // Tote mất hàng thì rời pallet; pallet đếm lại.
        if (parent?.ContainerType == ContainerTypes.Tote && parent.ParentMtuaNId is { } pallet)
        {
            var off = platform.CallCommand<RemoveFromMtua, ProductResponse>(new RemoveFromMtua { ChildNId = parent.NId });
            var recount = off.Succeeded ? PackingData.Recount(platform, pallet) : off;
            if (!recount.Succeeded)
            {
                return ShipResponse.From(recount);
            }
        }

        var packed = platform.CallCommand<AssembleIntoMtua, ProductResponse>(new AssembleIntoMtua { ChildNId = item.NId, ContainerNId = package.NId });
        if (packed.Succeeded && parent?.ContainerType == ContainerTypes.Tote)
        {
            packed = PackingData.Recount(platform, parent.NId);
        }

        if (!packed.Succeeded)
        {
            return ShipResponse.From(packed);
        }

        var full = platform.CallCommand<SetMtuaStatus, ProductResponse>(new SetMtuaStatus { NId = package.NId, Status = PalletStates.Full });
        return full.Succeeded ? new ShipResponse { NId = package.NId, Status = PalletStates.Full, Quantity = package.Quantity } : ShipResponse.From(full);
    }
}

/// <summary>Gỡ một sản phẩm khỏi gói lẻ, về <c>n/a</c>; hết hàng thì gói về Empty.</summary>
[Command<ShipOperationBlock>]
public sealed class RemoveItemFromCkdPackage : ICommand<ShipResponse>
{
    [Mandatory]
    public string? PackageNId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class RemoveItemFromCkdPackageHandler : ICommandHandler<RemoveItemFromCkdPackage, ShipResponse>
{
    public ShipResponse Handle(RemoveItemFromCkdPackage command, IPlatform platform)
    {
        if (!platform.Query<Mtu>().Any(m => m.NId == command.MtuNId && m.ParentMtuaNId == command.PackageNId))
        {
            return ShipResponse.Fail(NvErrors.UnknownMtu, $"{command.MtuNId} is not in package {command.PackageNId}.");
        }

        var removed = platform.CallCommand<RemoveFromMtua, ProductResponse>(new RemoveFromMtua { ChildNId = command.MtuNId });
        var recount = removed.Succeeded ? PackingData.Recount(platform, command.PackageNId!) : removed;
        if (!recount.Succeeded)
        {
            return ShipResponse.From(recount);
        }

        var package = platform.Query<Mtua>().Single(c => c.NId == command.PackageNId);
        return new ShipResponse { NId = package.NId, Status = package.Status, Quantity = package.Quantity };
    }
}

/// <summary>
/// Timer hằng ngày: pallet có hàng đo cuối quá <see cref="NvKeys.EolvRetestAfterDays"/> ngày thì đánh dấu hết hạn. Thiếu key thì
/// không làm gì, chỉ có dòng trace: lịch sử signal rule vẫn ghi lần chạy là thành công.
/// </summary>
[Command<ShipOperationBlock>]
public sealed class CheckEolvRetest : ICommand<ShipResponse>;

internal sealed class CheckEolvRetestHandler : ICommandHandler<CheckEolvRetest, ShipResponse>
{
    public ShipResponse Handle(CheckEolvRetest command, IPlatform platform)
    {
        var key = platform.ProjectionQuery<ConfigurationKey>().Where(k => k.NId == NvKeys.EolvRetestAfterDays).Select(k => k.Val).SingleOrDefault();
        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            platform.Tracer.Write(TraceLevel.Warning, $"Configuration key {NvKeys.EolvRetestAfterDays} is missing; no pallet checked.");
            return new ShipResponse();
        }

        var limit = platform.Time.GetUtcNow().AddDays(-days);
        var tests = platform.ProjectionQuery<PackingOperation>().Where(p => p.Action == PackingActions.Test).Select(p => p.NId).ToList();
        var expired = 0;
        foreach (var pallet in platform.Query<Mtua>().Where(c => c.ContainerType == ContainerTypes.Pallet && c.Status == PalletStates.Full).ToList())
        {
            var codes = PackingData.ItemsIn(platform, pallet.NId).Select(m => m.Code).Where(c => c != null).ToList();
            var tested = platform.Query<LineExecution>().Where(e => codes.Contains(e.Serial) && tests.Contains(e.OperationCode!) && e.StatusId == "OK")
                .Select(e => e.EndTime).ToList();
            if (tested.Count == 0 || tested.Min() >= limit)
            {
                continue;
            }

            var detail = PackingData.DetailOrNew(platform, pallet);
            if (!detail.EolvExpired)
            {
                detail.EolvExpired = true;
                platform.Submit(detail);
                expired++;
            }
        }

        platform.Tracer.Write(TraceLevel.Information, $"End-of-line test older than {days} day(s): {expired} pallet(s) marked expired.");
        return new ShipResponse { Quantity = expired };
    }
}
