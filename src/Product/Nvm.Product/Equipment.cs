using Nvm.Foundation;

namespace Nvm.Product;

/// <summary>Một nút của cây thiết bị. Cây có tám cấp; dự án dùng cả Plant và Subarea.</summary>
[Entity<ProductMasterBlock>]
public sealed class Equipment : Entity
{
    public string? Name { get; set; }

    /// <summary>Một trong <see cref="EquipmentLevels.Ordered"/>.</summary>
    [Mandatory]
    public string? Level { get; set; }

    /// <summary>Rỗng ở gốc cây (cấp Enterprise).</summary>
    public string? ParentNId { get; set; }
}

public static class EquipmentLevels
{
    /// <summary>Từ gốc xuống lá. Con phải ở cấp sâu hơn cha, không nhất thiết liền kề.</summary>
    public static readonly IReadOnlyList<string> Ordered =
        ["Enterprise", "Site", "Plant", "Area", "Subarea", "WorkCenter", "WorkUnit", "Machine"];

    /// <summary>Vị trí của hàng chưa có chỗ cụ thể: một equipment đặc biệt tên <c>n/a</c>.</summary>
    public const string NotApplicable = "n/a";
}

/// <summary>Property của equipment (vd tiền tố, cờ cấu hình). NId = <c>&lt;Equipment&gt;|&lt;Name&gt;</c>.</summary>
[Entity<ProductMasterBlock>]
public sealed class EquipmentProperty : Entity
{
    [Mandatory]
    public string? EquipmentNId { get; set; }

    [Mandatory]
    public string? Name { get; set; }

    [TextLength(-1)]
    public string? Value { get; set; }
}

/// <summary>Thành viên của một nhóm equipment (logical dock, nhóm route). NId = <c>&lt;Group&gt;|&lt;Equipment&gt;</c>.</summary>
[Entity<ProductMasterBlock>]
public sealed class EquipmentGroupMember : Entity
{
    [Mandatory]
    public string? GroupNId { get; set; }

    public string? GroupType { get; set; }

    [Mandatory]
    public string? EquipmentNId { get; set; }
}

[Command<ProductMasterBlock>]
public sealed class CreateEquipment : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    public string? Name { get; init; }

    [Mandatory]
    public string? Level { get; init; }

    public string? ParentNId { get; init; }
}

internal sealed class CreateEquipmentHandler : ICommandHandler<CreateEquipment, ProductResponse>
{
    public ProductResponse Handle(CreateEquipment command, IPlatform platform)
    {
        var depth = EquipmentLevels.Ordered.ToList().IndexOf(command.Level!);
        if (depth < 0)
        {
            return ProductResponse.Fail(ProductErrors.InvalidEquipmentLevel, $"Level {command.Level} is not an equipment level.");
        }

        if (platform.Query<Equipment>().Any(e => e.NId == command.NId))
        {
            return ProductResponse.Fail(ProductErrors.DuplicateNId, $"Equipment {command.NId} already exists.");
        }

        if (!string.IsNullOrEmpty(command.ParentNId))
        {
            var parent = platform.Query<Equipment>().SingleOrDefault(e => e.NId == command.ParentNId);
            if (parent is null)
            {
                return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Parent equipment {command.ParentNId} does not exist.");
            }

            if (EquipmentLevels.Ordered.ToList().IndexOf(parent.Level!) >= depth)
            {
                return ProductResponse.Fail(ProductErrors.InvalidEquipmentLevel,
                    $"{command.Level} cannot sit under {parent.Level} {parent.NId}.");
            }
        }

        var equipment = platform.Create<Equipment>();
        equipment.NId = command.NId!;
        equipment.Name = command.Name ?? command.NId;
        equipment.Level = command.Level;
        equipment.ParentNId = command.ParentNId;
        platform.Submit(equipment);
        return ProductResponse.Of(equipment);
    }
}

/// <summary>Tạo hoặc sửa một property của equipment.</summary>
[Command<ProductMasterBlock>]
public sealed class SetEquipmentProperty : ICommand<ProductResponse>
{
    [Mandatory]
    public string? EquipmentNId { get; init; }

    [Mandatory]
    public string? Name { get; init; }

    public string? Value { get; init; }
}

internal sealed class SetEquipmentPropertyHandler : ICommandHandler<SetEquipmentProperty, ProductResponse>
{
    public ProductResponse Handle(SetEquipmentProperty command, IPlatform platform)
    {
        if (!platform.Query<Equipment>().Any(e => e.NId == command.EquipmentNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {command.EquipmentNId} does not exist.");
        }

        var nid = command.EquipmentNId + "|" + command.Name;
        var property = platform.Query<EquipmentProperty>().SingleOrDefault(p => p.NId == nid) ?? platform.Create<EquipmentProperty>();
        property.NId = nid;
        property.EquipmentNId = command.EquipmentNId;
        property.Name = command.Name;
        property.Value = command.Value;
        platform.Submit(property);
        return ProductResponse.Of(property);
    }
}

[Command<ProductMasterBlock>]
public sealed class AddEquipmentToGroup : ICommand<ProductResponse>
{
    [Mandatory]
    public string? GroupNId { get; init; }

    public string? GroupType { get; init; }

    [Mandatory]
    public string? EquipmentNId { get; init; }
}

internal sealed class AddEquipmentToGroupHandler : ICommandHandler<AddEquipmentToGroup, ProductResponse>
{
    public ProductResponse Handle(AddEquipmentToGroup command, IPlatform platform)
    {
        if (!platform.Query<Equipment>().Any(e => e.NId == command.EquipmentNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {command.EquipmentNId} does not exist.");
        }

        var nid = command.GroupNId + "|" + command.EquipmentNId;
        var member = platform.Query<EquipmentGroupMember>().SingleOrDefault(m => m.NId == nid) ?? platform.Create<EquipmentGroupMember>();
        member.NId = nid;
        member.GroupNId = command.GroupNId;
        member.GroupType = command.GroupType;
        member.EquipmentNId = command.EquipmentNId;
        platform.Submit(member);
        return ProductResponse.Of(member);
    }
}
