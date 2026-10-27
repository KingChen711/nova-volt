using System.Globalization;
using Nvm.Foundation;

namespace Nvm.Product;

/// <summary>
/// Material Tracking Unit: một chiếc có serial (số lượng 1) hoặc một lô có số lượng. Vị trí là một equipment, hoặc một
/// MTUA chứa nó; khi nằm trong MTUA thì ô equipment để trống.
/// </summary>
[Entity<ProductOperationBlock>]
public sealed class Mtu : Entity
{
    [Mandatory]
    public string? MaterialNId { get; set; }

    /// <summary><c>Serial</c> hoặc <c>Batch</c>, theo cờ <see cref="Material.Serialized"/> lúc tạo.</summary>
    public string? Kind { get; set; }

    public decimal Quantity { get; set; }

    public string? UnitOfMeasure { get; set; }

    /// <summary>Một trong <see cref="MtuStatus"/>.</summary>
    public string? Status { get; set; }

    public string? EquipmentNId { get; set; }

    public string? ParentMtuaNId { get; set; }

    /// <summary>MTU đang nằm trong một containment request chưa nhả.</summary>
    public bool InContainment { get; set; }
}

/// <summary>Vật chứa (tote, pallet, trailer kho): gom MTU và MTUA khác, lồng nhiều tầng.</summary>
[Entity<ProductOperationBlock>]
public sealed class Mtua : Entity
{
    public string? ContainerType { get; set; }

    public string? EquipmentNId { get; set; }

    public string? ParentMtuaNId { get; set; }
}

/// <summary>Lịch sử MTU: chỉ thêm dòng, không sửa (K4). Dòng Assemble của sản phẩm là genealogy.</summary>
[Entity<ProductOperationBlock>]
public sealed class MtuHistory : Entity
{
    [Mandatory]
    public string? MtuNId { get; set; }

    /// <summary><c>Create</c>, <c>Assemble</c> hoặc <c>Edit</c>.</summary>
    [Mandatory]
    public string? Action { get; set; }

    public DateTimeOffset OccurredOn { get; set; }

    public decimal? Quantity { get; set; }

    /// <summary>Với Assemble của sản phẩm: MTU nguyên liệu đã trừ.</summary>
    public string? ComponentMtuNId { get; set; }

    /// <summary>Với Assemble vào vật chứa: MTUA nhận nó.</summary>
    public string? ContainerNId { get; set; }

    public string? WorkOrderNId { get; set; }

    public string? Detail { get; set; }

    public string? User { get; set; }
}

public static class MtuHistoryActions
{
    public const string Create = "Create";
    public const string Assemble = "Assemble";
    public const string Edit = "Edit";
}

internal static class History
{
    public static void Write(IPlatform platform, string mtu, string action, string? detail = null, decimal? quantity = null,
        string? component = null, string? container = null, string? workOrder = null)
    {
        var row = platform.Create<MtuHistory>();
        row.NId = row.Id.ToString("N");
        row.MtuNId = mtu;
        row.Action = action;
        row.OccurredOn = platform.Time.GetUtcNow();
        row.Quantity = quantity;
        row.ComponentMtuNId = component;
        row.ContainerNId = container;
        row.WorkOrderNId = workOrder;
        row.Detail = detail;
        row.User = platform.Caller.User;
        platform.Submit(row);
    }

    /// <summary>Bỏ số 0 thừa của cột <c>decimal(18,6)</c>: 2.000000 thành 2.</summary>
    public static string Format(decimal value) => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Tra vị trí: đi ngược qua các vật chứa tới equipment của tầng ngoài cùng.</summary>
public static class MtuLocations
{
    public static string? EquipmentOf(IQueryable<Mtu> mtus, IQueryable<Mtua> containers, string nid)
    {
        ArgumentNullException.ThrowIfNull(mtus);
        ArgumentNullException.ThrowIfNull(containers);
        var mtu = mtus.SingleOrDefault(m => m.NId == nid);
        var (equipment, parent) = mtu is not null
            ? (mtu.EquipmentNId, mtu.ParentMtuaNId)
            : containers.Where(c => c.NId == nid).Select(c => ValueTuple.Create(c.EquipmentNId, c.ParentMtuaNId)).SingleOrDefault();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(parent) && seen.Add(parent))
        {
            var container = containers.Single(c => c.NId == parent);
            (equipment, parent) = (container.EquipmentNId, container.ParentMtuaNId);
        }

        return equipment;
    }
}

[Command<ProductOperationBlock>]
public sealed class CreateMtu : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    /// <summary>Serial: rỗng hoặc 1. Batch: số lượng của lô.</summary>
    public decimal? Quantity { get; init; }

    /// <summary>Rỗng: equipment <c>n/a</c>.</summary>
    public string? EquipmentNId { get; init; }
}

internal sealed class CreateMtuHandler : ICommandHandler<CreateMtu, ProductResponse>
{
    public ProductResponse Handle(CreateMtu command, IPlatform platform) =>
        Mtus.Create(platform, command.NId!, command.MaterialNId!, command.Quantity, command.EquipmentNId, workOrder: null);
}

internal static class Mtus
{
    public static ProductResponse Create(IPlatform platform, string nid, string materialNId, decimal? quantity, string? equipmentNId,
        string? workOrder)
    {
        // Material, Equipment thuộc domain master data: đọc bằng ProjectionQuery, chỉ thấy bản đã commit.
        var material = platform.ProjectionQuery<Material>().SingleOrDefault(m => m.NId == materialNId);
        if (material is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownMaterial, $"Material {materialNId} does not exist.");
        }

        if (material.Serialized && quantity is not (null or 1m))
        {
            return ProductResponse.Fail(ProductErrors.SerialQuantity, $"Material {materialNId} is serialized: quantity must be 1.");
        }

        if (quantity < 0)
        {
            return ProductResponse.Fail(ProductErrors.InvalidQuantity, "Quantity cannot be negative.");
        }

        if (!string.IsNullOrEmpty(equipmentNId) && equipmentNId != EquipmentLevels.NotApplicable
            && !platform.ProjectionQuery<Equipment>().Any(e => e.NId == equipmentNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {equipmentNId} does not exist.");
        }

        if (platform.Query<Mtu>().Any(m => m.NId == nid))
        {
            return ProductResponse.Fail(ProductErrors.DuplicateNId, $"MTU {nid} already exists.");
        }

        var mtu = platform.Create<Mtu>();
        mtu.NId = nid;
        mtu.MaterialNId = materialNId;
        mtu.Kind = material.Serialized ? "Serial" : "Batch";
        mtu.Quantity = material.Serialized ? 1m : quantity ?? 0m;
        mtu.UnitOfMeasure = material.UnitOfMeasure;
        mtu.Status = MtuStatus.Available;
        mtu.EquipmentNId = string.IsNullOrEmpty(equipmentNId) ? EquipmentLevels.NotApplicable : equipmentNId;
        platform.Submit(mtu);
        History.Write(platform, nid, MtuHistoryActions.Create, quantity: mtu.Quantity, workOrder: workOrder);
        return ProductResponse.Of(mtu);
    }

    public static ProductResponse? Find(IPlatform platform, string nid, out Mtu mtu)
    {
        mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == nid)!;
        return mtu is null ? ProductResponse.Fail(ProductErrors.UnknownMtu, $"MTU {nid} does not exist.") : null;
    }
}

/// <summary>Sửa số lượng của MTU (giá trị mới, không phải phần cộng thêm).</summary>
[Command<ProductOperationBlock>]
public sealed class EditMtu : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public decimal? Quantity { get; init; }
}

internal sealed class EditMtuHandler : ICommandHandler<EditMtu, ProductResponse>
{
    public ProductResponse Handle(EditMtu command, IPlatform platform)
    {
        if (Mtus.Find(platform, command.NId!, out var mtu) is { } missing)
        {
            return missing;
        }

        if (mtu.Kind == "Serial" && command.Quantity != 1m)
        {
            return ProductResponse.Fail(ProductErrors.SerialQuantity, $"MTU {mtu.NId} is serialized: quantity must be 1.");
        }

        if (command.Quantity < 0)
        {
            return ProductResponse.Fail(ProductErrors.InvalidQuantity, "Quantity cannot be negative.");
        }

        var before = mtu.Quantity;
        mtu.Quantity = command.Quantity!.Value;
        platform.Submit(mtu);
        History.Write(platform, mtu.NId, MtuHistoryActions.Edit, $"Quantity {History.Format(before)} -> {History.Format(mtu.Quantity)}",
            mtu.Quantity);
        return ProductResponse.Of(mtu);
    }
}

/// <summary>Đặt MTU ở một equipment. MTU đang nằm trong vật chứa thì phải gỡ ra trước.</summary>
[Command<ProductOperationBlock>]
public sealed class MoveMtu : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? EquipmentNId { get; init; }
}

internal sealed class MoveMtuHandler : ICommandHandler<MoveMtu, ProductResponse>
{
    public ProductResponse Handle(MoveMtu command, IPlatform platform)
    {
        if (Mtus.Find(platform, command.NId!, out var mtu) is { } missing)
        {
            return missing;
        }

        if (!string.IsNullOrEmpty(mtu.ParentMtuaNId))
        {
            return ProductResponse.Fail(ProductErrors.InsideContainer, $"MTU {mtu.NId} is inside {mtu.ParentMtuaNId}; remove it first.");
        }

        if (!platform.ProjectionQuery<Equipment>().Any(e => e.NId == command.EquipmentNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {command.EquipmentNId} does not exist.");
        }

        var before = mtu.EquipmentNId;
        mtu.EquipmentNId = command.EquipmentNId;
        platform.Submit(mtu);
        History.Write(platform, mtu.NId, MtuHistoryActions.Edit, $"Equipment {before} -> {mtu.EquipmentNId}");
        return ProductResponse.Of(mtu);
    }
}

/// <summary>Tách một phần lô ra MTU con <c>&lt;NId&gt;.&lt;n&gt;</c> cùng material, cùng vị trí.</summary>
[Command<ProductOperationBlock>]
public sealed class SplitMtu : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public decimal? Quantity { get; init; }
}

internal sealed class SplitMtuHandler : ICommandHandler<SplitMtu, ProductResponse>
{
    public ProductResponse Handle(SplitMtu command, IPlatform platform)
    {
        if (Mtus.Find(platform, command.NId!, out var parent) is { } missing)
        {
            return missing;
        }

        var quantity = command.Quantity!.Value;
        if (quantity <= 0 || quantity >= parent.Quantity)
        {
            return ProductResponse.Fail(ProductErrors.InvalidQuantity,
                $"Split quantity must be between 0 and {History.Format(parent.Quantity)} (exclusive).");
        }

        var prefix = parent.NId + ".";
        var siblings = platform.Query<Mtu>().Where(m => m.NId.StartsWith(prefix)).Select(m => m.NId).ToList();
        var index = 1;
        while (siblings.Contains(prefix + index.ToString(CultureInfo.InvariantCulture)))
        {
            index++;
        }

        var child = platform.Create<Mtu>();
        child.NId = prefix + index.ToString(CultureInfo.InvariantCulture);
        child.MaterialNId = parent.MaterialNId;
        child.Kind = parent.Kind;
        child.Quantity = quantity;
        child.UnitOfMeasure = parent.UnitOfMeasure;
        child.Status = parent.Status;
        child.EquipmentNId = parent.EquipmentNId;
        child.ParentMtuaNId = parent.ParentMtuaNId;
        platform.Submit(child);

        parent.Quantity -= quantity;
        platform.Submit(parent);
        History.Write(platform, child.NId, MtuHistoryActions.Create, $"Split from {parent.NId}", quantity);
        History.Write(platform, parent.NId, MtuHistoryActions.Edit, $"Split {History.Format(quantity)} to {child.NId}", parent.Quantity);
        return ProductResponse.Of(child);
    }
}

[Command<ProductOperationBlock>]
public sealed class CreateMtua : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    /// <summary>Vd <c>Tote</c>, <c>Pallet</c>, <c>StorageTrailer</c>.</summary>
    [Mandatory]
    public string? ContainerType { get; init; }

    public string? EquipmentNId { get; init; }
}

internal sealed class CreateMtuaHandler : ICommandHandler<CreateMtua, ProductResponse>
{
    public ProductResponse Handle(CreateMtua command, IPlatform platform)
    {
        if (platform.Query<Mtua>().Any(c => c.NId == command.NId))
        {
            return ProductResponse.Fail(ProductErrors.DuplicateNId, $"MTUA {command.NId} already exists.");
        }

        if (!string.IsNullOrEmpty(command.EquipmentNId) && !platform.ProjectionQuery<Equipment>().Any(e => e.NId == command.EquipmentNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {command.EquipmentNId} does not exist.");
        }

        var container = platform.Create<Mtua>();
        container.NId = command.NId!;
        container.ContainerType = command.ContainerType;
        container.EquipmentNId = string.IsNullOrEmpty(command.EquipmentNId) ? EquipmentLevels.NotApplicable : command.EquipmentNId;
        platform.Submit(container);
        return ProductResponse.Of(container);
    }
}

/// <summary>
/// Gắn một MTU hoặc MTUA vào vật chứa. Lớp Product chỉ gắn: gắn MTU thẳng vào pallet đang chứa tote thì tote vẫn ở đó;
/// tháo hết tote là việc của post-action phía dự án.
/// </summary>
[Command<ProductOperationBlock>]
public sealed class AssembleIntoMtua : ICommand<ProductResponse>
{
    /// <summary>NId của MTU hoặc MTUA.</summary>
    [Mandatory]
    public string? ChildNId { get; init; }

    [Mandatory]
    public string? ContainerNId { get; init; }
}

internal sealed class AssembleIntoMtuaHandler : ICommandHandler<AssembleIntoMtua, ProductResponse>
{
    public ProductResponse Handle(AssembleIntoMtua command, IPlatform platform)
    {
        var containers = platform.Query<Mtua>();
        if (!containers.Any(c => c.NId == command.ContainerNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownContainer, $"MTUA {command.ContainerNId} does not exist.");
        }

        var mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == command.ChildNId);
        if (mtu is not null)
        {
            mtu.ParentMtuaNId = command.ContainerNId;
            mtu.EquipmentNId = null;
            platform.Submit(mtu);
            History.Write(platform, mtu.NId, MtuHistoryActions.Assemble, container: command.ContainerNId);
            return ProductResponse.Of(mtu);
        }

        var child = containers.SingleOrDefault(c => c.NId == command.ChildNId);
        if (child is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownMtu, $"{command.ChildNId} is neither an MTU nor an MTUA.");
        }

        // Không cho vật chứa nằm trong chính nó, trực tiếp hay qua nhiều tầng.
        for (var up = command.ContainerNId; !string.IsNullOrEmpty(up); up = containers.Where(c => c.NId == up).Select(c => c.ParentMtuaNId).Single())
        {
            if (up == child.NId)
            {
                return ProductResponse.Fail(ProductErrors.ContainerCycle, $"{child.NId} cannot go inside {command.ContainerNId}.");
            }
        }

        child.ParentMtuaNId = command.ContainerNId;
        child.EquipmentNId = null;
        platform.Submit(child);
        return ProductResponse.Of(child);
    }
}

/// <summary>Gỡ MTU hoặc MTUA khỏi vật chứa, đặt ở một equipment (rỗng: <c>n/a</c>).</summary>
[Command<ProductOperationBlock>]
public sealed class RemoveFromMtua : ICommand<ProductResponse>
{
    [Mandatory]
    public string? ChildNId { get; init; }

    public string? EquipmentNId { get; init; }
}

internal sealed class RemoveFromMtuaHandler : ICommandHandler<RemoveFromMtua, ProductResponse>
{
    public ProductResponse Handle(RemoveFromMtua command, IPlatform platform)
    {
        var equipment = string.IsNullOrEmpty(command.EquipmentNId) ? EquipmentLevels.NotApplicable : command.EquipmentNId;
        if (equipment != EquipmentLevels.NotApplicable && !platform.ProjectionQuery<Equipment>().Any(e => e.NId == equipment))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {equipment} does not exist.");
        }

        var mtu = platform.Query<Mtu>().SingleOrDefault(m => m.NId == command.ChildNId);
        if (mtu is not null)
        {
            var from = mtu.ParentMtuaNId;
            mtu.ParentMtuaNId = null;
            mtu.EquipmentNId = equipment;
            platform.Submit(mtu);
            History.Write(platform, mtu.NId, MtuHistoryActions.Edit, $"Removed from {from} to {equipment}");
            return ProductResponse.Of(mtu);
        }

        var child = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.ChildNId);
        if (child is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownMtu, $"{command.ChildNId} is neither an MTU nor an MTUA.");
        }

        child.ParentMtuaNId = null;
        child.EquipmentNId = equipment;
        platform.Submit(child);
        return ProductResponse.Of(child);
    }
}
