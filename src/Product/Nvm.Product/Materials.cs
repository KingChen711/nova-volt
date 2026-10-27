using Nvm.Foundation;

namespace Nvm.Product;

/// <summary>Định nghĩa một loại hàng: tên, đơn vị, hàng thuộc loại này có đánh serial hay không.</summary>
[Entity<ProductMasterBlock>]
public sealed class Material : Entity
{
    public string? Name { get; set; }

    [Mandatory]
    public string? UnitOfMeasure { get; set; }

    /// <summary>Bật: MTU của material là từng chiếc, số lượng 1. Tắt: MTU là lô, số lượng tùy.</summary>
    public bool Serialized { get; set; }

    public string? MaterialClass { get; set; }
}

/// <summary>Phiên bản của material; đúng một bản <see cref="IsCurrent"/>. NId = <c>&lt;Material&gt;:&lt;Revision&gt;</c>.</summary>
[Entity<ProductMasterBlock>]
public sealed class MaterialRevision : Entity
{
    [Mandatory]
    public string? MaterialNId { get; set; }

    [Mandatory]
    public string? Revision { get; set; }

    public bool IsCurrent { get; set; }
}

/// <summary>Process của một BoP: sinh ra <see cref="ProductMaterialNId"/>. Dữ liệu dự án thường đặt BoP và Process trùng tên.</summary>
[Entity<ProductMasterBlock>]
public sealed class Process : Entity
{
    [Mandatory]
    public string? BopNId { get; set; }

    [Mandatory]
    public string? ProductMaterialNId { get; set; }
}

/// <summary>Một bước của process, xếp theo <see cref="Sequence"/>. NId = <c>&lt;Process&gt;:&lt;Operation&gt;</c>.</summary>
[Entity<ProductMasterBlock>]
public sealed class ProcessOperation : Entity
{
    [Mandatory]
    public string? ProcessNId { get; set; }

    [Mandatory]
    public string? OperationNId { get; set; }

    public int Sequence { get; set; }

    public string? Name { get; set; }
}

/// <summary>Máy được phép chạy một bước; một bước có thể có nhiều máy.</summary>
[Entity<ProductMasterBlock>]
public sealed class OperationEquipment : Entity
{
    [Mandatory]
    public string? ProcessOperationNId { get; set; }

    [Mandatory]
    public string? EquipmentNId { get; set; }
}

/// <summary>Nguyên liệu định mức của một bước, tính cho một đơn vị sản phẩm.</summary>
[Entity<ProductMasterBlock>]
public sealed class OperationMaterial : Entity
{
    [Mandatory]
    public string? ProcessOperationNId { get; set; }

    [Mandatory]
    public string? MaterialNId { get; set; }

    public decimal Quantity { get; set; }

    /// <summary><c>NormalPart</c>, <c>AsRequired</c> hoặc <c>Alternative</c>.</summary>
    public string? Usage { get; set; }
}

[Command<ProductMasterBlock>]
public sealed class CreateMaterial : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    public string? Name { get; init; }

    [Mandatory]
    public string? UnitOfMeasure { get; init; }

    public bool Serialized { get; init; }

    public string? MaterialClass { get; init; }
}

internal sealed class CreateMaterialHandler : ICommandHandler<CreateMaterial, ProductResponse>
{
    public ProductResponse Handle(CreateMaterial command, IPlatform platform)
    {
        if (platform.Query<Material>().Any(m => m.NId == command.NId))
        {
            return ProductResponse.Fail(ProductErrors.DuplicateNId, $"Material {command.NId} already exists.");
        }

        var material = platform.Create<Material>();
        material.NId = command.NId!;
        material.Name = command.Name ?? command.NId;
        material.UnitOfMeasure = command.UnitOfMeasure;
        material.Serialized = command.Serialized;
        material.MaterialClass = command.MaterialClass;
        platform.Submit(material);
        return ProductResponse.Of(material);
    }
}

/// <summary>Thêm revision. <see cref="IsCurrent"/> bật thì revision hiện hành cũ tắt cờ.</summary>
[Command<ProductMasterBlock>]
public sealed class CreateMaterialRevision : ICommand<ProductResponse>
{
    [Mandatory]
    public string? MaterialNId { get; init; }

    [Mandatory]
    public string? Revision { get; init; }

    public bool IsCurrent { get; init; }
}

internal sealed class CreateMaterialRevisionHandler : ICommandHandler<CreateMaterialRevision, ProductResponse>
{
    public ProductResponse Handle(CreateMaterialRevision command, IPlatform platform)
    {
        if (!platform.Query<Material>().Any(m => m.NId == command.MaterialNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownMaterial, $"Material {command.MaterialNId} does not exist.");
        }

        if (command.IsCurrent)
        {
            foreach (var old in platform.Query<MaterialRevision>().Where(r => r.MaterialNId == command.MaterialNId && r.IsCurrent).ToList())
            {
                old.IsCurrent = false;
                platform.Submit(old);
            }
        }

        var revision = platform.Create<MaterialRevision>();
        revision.NId = command.MaterialNId + ":" + command.Revision;
        revision.MaterialNId = command.MaterialNId;
        revision.Revision = command.Revision;
        revision.IsCurrent = command.IsCurrent;
        platform.Submit(revision);
        return ProductResponse.Of(revision);
    }
}

[Command<ProductMasterBlock>]
public sealed class CreateProcess : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    /// <summary>Rỗng: BoP trùng tên process, như dữ liệu dự án.</summary>
    public string? BopNId { get; init; }

    [Mandatory]
    public string? ProductMaterialNId { get; init; }
}

internal sealed class CreateProcessHandler : ICommandHandler<CreateProcess, ProductResponse>
{
    public ProductResponse Handle(CreateProcess command, IPlatform platform)
    {
        if (!platform.Query<Material>().Any(m => m.NId == command.ProductMaterialNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownMaterial, $"Material {command.ProductMaterialNId} does not exist.");
        }

        if (platform.Query<Process>().Any(p => p.NId == command.NId))
        {
            return ProductResponse.Fail(ProductErrors.DuplicateNId, $"Process {command.NId} already exists.");
        }

        var process = platform.Create<Process>();
        process.NId = command.NId!;
        process.BopNId = string.IsNullOrEmpty(command.BopNId) ? command.NId : command.BopNId;
        process.ProductMaterialNId = command.ProductMaterialNId;
        platform.Submit(process);
        return ProductResponse.Of(process);
    }
}

[Command<ProductMasterBlock>]
public sealed class AddProcessOperation : ICommand<ProductResponse>
{
    [Mandatory]
    public string? ProcessNId { get; init; }

    [Mandatory]
    public string? OperationNId { get; init; }

    public int Sequence { get; init; }

    public string? Name { get; init; }
}

internal sealed class AddProcessOperationHandler : ICommandHandler<AddProcessOperation, ProductResponse>
{
    public ProductResponse Handle(AddProcessOperation command, IPlatform platform)
    {
        if (!platform.Query<Process>().Any(p => p.NId == command.ProcessNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownProcess, $"Process {command.ProcessNId} does not exist.");
        }

        var operation = platform.Create<ProcessOperation>();
        operation.NId = command.ProcessNId + ":" + command.OperationNId;
        operation.ProcessNId = command.ProcessNId;
        operation.OperationNId = command.OperationNId;
        operation.Sequence = command.Sequence;
        operation.Name = command.Name;
        platform.Submit(operation);
        return ProductResponse.Of(operation);
    }
}

[Command<ProductMasterBlock>]
public sealed class AllowOperationEquipment : ICommand<ProductResponse>
{
    [Mandatory]
    public string? ProcessOperationNId { get; init; }

    [Mandatory]
    public string? EquipmentNId { get; init; }
}

internal sealed class AllowOperationEquipmentHandler : ICommandHandler<AllowOperationEquipment, ProductResponse>
{
    public ProductResponse Handle(AllowOperationEquipment command, IPlatform platform)
    {
        if (!platform.Query<ProcessOperation>().Any(o => o.NId == command.ProcessOperationNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownProcess, $"Operation {command.ProcessOperationNId} does not exist.");
        }

        if (!platform.Query<Equipment>().Any(e => e.NId == command.EquipmentNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {command.EquipmentNId} does not exist.");
        }

        var link = platform.Create<OperationEquipment>();
        link.NId = command.ProcessOperationNId + "|" + command.EquipmentNId;
        link.ProcessOperationNId = command.ProcessOperationNId;
        link.EquipmentNId = command.EquipmentNId;
        platform.Submit(link);
        return ProductResponse.Of(link);
    }
}

[Command<ProductMasterBlock>]
public sealed class AddOperationMaterial : ICommand<ProductResponse>
{
    [Mandatory]
    public string? ProcessOperationNId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    public decimal Quantity { get; init; }

    public string? Usage { get; init; }
}

internal sealed class AddOperationMaterialHandler : ICommandHandler<AddOperationMaterial, ProductResponse>
{
    public ProductResponse Handle(AddOperationMaterial command, IPlatform platform)
    {
        if (!platform.Query<ProcessOperation>().Any(o => o.NId == command.ProcessOperationNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownProcess, $"Operation {command.ProcessOperationNId} does not exist.");
        }

        if (!platform.Query<Material>().Any(m => m.NId == command.MaterialNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownMaterial, $"Material {command.MaterialNId} does not exist.");
        }

        var line = platform.Create<OperationMaterial>();
        line.NId = command.ProcessOperationNId + "|" + command.MaterialNId;
        line.ProcessOperationNId = command.ProcessOperationNId;
        line.MaterialNId = command.MaterialNId;
        line.Quantity = command.Quantity;
        line.Usage = command.Usage ?? "NormalPart";
        platform.Submit(line);
        return ProductResponse.Of(line);
    }
}
