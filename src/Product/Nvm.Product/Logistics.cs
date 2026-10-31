using Nvm.Foundation;

namespace Nvm.Product;

/// <summary>Chỗ chứa hàng thật (ô trên sàn, khu công đoạn), đặt tại một equipment.</summary>
[Entity<ProductMasterBlock>]
public sealed class Buffer : Entity
{
    [Mandatory]
    public string? EquipmentNId { get; set; }
}

/// <summary>Material được phép vào buffer. Buffer không có dòng nào thì nhận mọi material.</summary>
[Entity<ProductMasterBlock>]
public sealed class BufferWhitelist : Entity
{
    [Mandatory]
    public string? BufferNId { get; set; }

    [Mandatory]
    public string? MaterialNId { get; set; }
}

/// <summary>Lô đang thuộc buffer nào. NId = NId của MTU, nên mỗi lô chỉ thuộc một buffer.</summary>
[Entity<ProductOperationBlock>]
public sealed class BufferAssignment : Entity
{
    [Mandatory]
    public string? BufferNId { get; set; }

    [Mandatory]
    public string? MtuNId { get; set; }
}

[Command<ProductMasterBlock>]
public sealed class CreateBuffer : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? EquipmentNId { get; init; }
}

internal sealed class CreateBufferHandler : ICommandHandler<CreateBuffer, ProductResponse>
{
    public ProductResponse Handle(CreateBuffer command, IPlatform platform)
    {
        if (!platform.Query<Equipment>().Any(e => e.NId == command.EquipmentNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {command.EquipmentNId} does not exist.");
        }

        if (platform.Query<Buffer>().Any(b => b.NId == command.NId))
        {
            return ProductResponse.Fail(ProductErrors.DuplicateNId, $"Buffer {command.NId} already exists.");
        }

        var buffer = platform.Create<Buffer>();
        buffer.NId = command.NId!;
        buffer.EquipmentNId = command.EquipmentNId;
        platform.Submit(buffer);
        return ProductResponse.Of(buffer);
    }
}

[Command<ProductMasterBlock>]
public sealed class AddBufferWhitelist : ICommand<ProductResponse>
{
    [Mandatory]
    public string? BufferNId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }
}

internal sealed class AddBufferWhitelistHandler : ICommandHandler<AddBufferWhitelist, ProductResponse>
{
    public ProductResponse Handle(AddBufferWhitelist command, IPlatform platform)
    {
        if (!platform.Query<Buffer>().Any(b => b.NId == command.BufferNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownBuffer, $"Buffer {command.BufferNId} does not exist.");
        }

        var nid = command.BufferNId + "|" + command.MaterialNId;
        var line = platform.Query<BufferWhitelist>().SingleOrDefault(w => w.NId == nid) ?? platform.Create<BufferWhitelist>();
        line.NId = nid;
        line.BufferNId = command.BufferNId;
        line.MaterialNId = command.MaterialNId;
        platform.Submit(line);
        return ProductResponse.Of(line);
    }
}

/// <summary>Gắn lô vào buffer. Lô còn thuộc buffer khác thì phải gỡ trước; material ngoài whitelist bị từ chối.</summary>
[Command<ProductOperationBlock>]
public sealed class AttachMtuToBuffer : ICommand<ProductResponse>
{
    [Mandatory]
    public string? BufferNId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class AttachMtuToBufferHandler : ICommandHandler<AttachMtuToBuffer, ProductResponse>
{
    public ProductResponse Handle(AttachMtuToBuffer command, IPlatform platform)
    {
        if (!platform.ProjectionQuery<Buffer>().Any(b => b.NId == command.BufferNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownBuffer, $"Buffer {command.BufferNId} does not exist.");
        }

        if (Mtus.Find(platform, command.MtuNId!, out var mtu) is { } missing)
        {
            return missing;
        }

        var current = platform.Query<BufferAssignment>().SingleOrDefault(a => a.NId == mtu.NId);
        if (current is not null)
        {
            return ProductResponse.Fail(ProductErrors.AlreadyInBuffer, $"MTU {mtu.NId} already belongs to buffer {current.BufferNId}; detach it first.");
        }

        var whitelist = platform.ProjectionQuery<BufferWhitelist>().Where(w => w.BufferNId == command.BufferNId).Select(w => w.MaterialNId).ToList();
        if (whitelist.Count > 0 && !whitelist.Contains(mtu.MaterialNId))
        {
            return ProductResponse.Fail(ProductErrors.NotWhitelisted, $"Material {mtu.MaterialNId} is not allowed in buffer {command.BufferNId}.");
        }

        var assignment = platform.Create<BufferAssignment>();
        assignment.NId = mtu.NId;
        assignment.BufferNId = command.BufferNId;
        assignment.MtuNId = mtu.NId;
        platform.Submit(assignment);
        return ProductResponse.Of(assignment);
    }
}

[Command<ProductOperationBlock>]
public sealed class DetachMtuFromBuffer : ICommand<ProductResponse>
{
    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class DetachMtuFromBufferHandler : ICommandHandler<DetachMtuFromBuffer, ProductResponse>
{
    public ProductResponse Handle(DetachMtuFromBuffer command, IPlatform platform)
    {
        var assignment = platform.Query<BufferAssignment>().SingleOrDefault(a => a.NId == command.MtuNId);
        if (assignment is null)
        {
            return ProductResponse.Fail(ProductErrors.NotInBuffer, $"MTU {command.MtuNId} is not in any buffer.");
        }

        platform.Delete(assignment);
        return ProductResponse.Of(assignment);
    }
}

/// <summary>
/// Gắn lô vào buffer, không xét whitelist. Lô còn thuộc buffer khác vẫn bị từ chối: lệnh chỉ gắn, không chuyển giữa hai buffer.
/// </summary>
[Command<ProductOperationBlock>]
public sealed class LinkMtuToBuffer : ICommand<ProductResponse>
{
    [Mandatory]
    public string? BufferNId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }
}

internal sealed class LinkMtuToBufferHandler : ICommandHandler<LinkMtuToBuffer, ProductResponse>
{
    public ProductResponse Handle(LinkMtuToBuffer command, IPlatform platform)
    {
        if (!platform.ProjectionQuery<Buffer>().Any(b => b.NId == command.BufferNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownBuffer, $"Buffer {command.BufferNId} does not exist.");
        }

        if (Mtus.Find(platform, command.MtuNId!, out var mtu) is { } missing)
        {
            return missing;
        }

        var current = platform.Query<BufferAssignment>().SingleOrDefault(a => a.NId == mtu.NId);
        if (current is not null)
        {
            return ProductResponse.Fail(ProductErrors.AlreadyInBuffer, $"Cannot link {mtu.NId}: it is already linked to buffer {current.BufferNId}.");
        }

        var assignment = platform.Create<BufferAssignment>();
        assignment.NId = mtu.NId;
        assignment.BufferNId = command.BufferNId;
        assignment.MtuNId = mtu.NId;
        platform.Submit(assignment);
        return ProductResponse.Of(assignment);
    }
}

/// <summary>Yêu cầu một lượng material từ buffer nguồn tới buffer đích. Chưa nói lô nào: lô do người chở quét.</summary>
[Entity<ProductOperationBlock>]
public sealed class LogisticRequest : Entity
{
    [Mandatory]
    public string? MaterialNId { get; set; }

    public decimal Quantity { get; set; }

    public string? SourceBufferNId { get; set; }

    [Mandatory]
    public string? DestinationBufferNId { get; set; }

    /// <summary>Do người dùng sản phẩm đặt; rỗng là không có.</summary>
    public int? Priority { get; set; }
}

/// <summary>Việc chở của một yêu cầu.</summary>
[Entity<ProductOperationBlock>]
public sealed class TransportOperation : Entity
{
    [Mandatory]
    public string? LogisticRequestNId { get; set; }

    public string? MaterialNId { get; set; }

    public decimal Quantity { get; set; }

    public string? SourceBufferNId { get; set; }

    public string? DestinationBufferNId { get; set; }

    /// <summary>Một trong <see cref="TransportStatus"/>.</summary>
    public string? Status { get; set; }
}

public static class TransportStatus
{
    public const string Released = "Released";
    public const string Active = "Active";
    public const string Complete = "Complete";
    public const string Cancelled = "Cancelled";
}

[Command<ProductOperationBlock>]
public sealed class CreateLogisticRequest : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? MaterialNId { get; init; }

    public decimal Quantity { get; init; }

    public string? SourceBufferNId { get; init; }

    [Mandatory]
    public string? DestinationBufferNId { get; init; }

    public int? Priority { get; init; }
}

internal sealed class CreateLogisticRequestHandler : ICommandHandler<CreateLogisticRequest, ProductResponse>
{
    public ProductResponse Handle(CreateLogisticRequest command, IPlatform platform)
    {
        if (!platform.ProjectionQuery<Material>().Any(m => m.NId == command.MaterialNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownMaterial, $"Material {command.MaterialNId} does not exist.");
        }

        if (command.Quantity <= 0)
        {
            return ProductResponse.Fail(ProductErrors.InvalidQuantity, "Quantity must be greater than zero.");
        }

        foreach (var buffer in new[] { command.SourceBufferNId, command.DestinationBufferNId }.Where(b => b is not null))
        {
            if (!platform.ProjectionQuery<Buffer>().Any(b => b.NId == buffer))
            {
                return ProductResponse.Fail(ProductErrors.UnknownBuffer, $"Buffer {buffer} does not exist.");
            }
        }

        var request = platform.Create<LogisticRequest>();
        request.NId = command.NId!;
        request.MaterialNId = command.MaterialNId;
        request.Quantity = command.Quantity;
        request.SourceBufferNId = command.SourceBufferNId;
        request.DestinationBufferNId = command.DestinationBufferNId;
        request.Priority = command.Priority;
        platform.Submit(request);
        return ProductResponse.Of(request);
    }
}

/// <summary>Nhận yêu cầu: sinh việc chở ở Released.</summary>
[Command<ProductOperationBlock>]
public sealed class AcceptMaterialRequest : ICommand<ProductResponse>
{
    [Mandatory]
    public string? LogisticRequestNId { get; init; }

    [Mandatory]
    public string? TransportOperationNId { get; init; }
}

internal sealed class AcceptMaterialRequestHandler : ICommandHandler<AcceptMaterialRequest, ProductResponse>
{
    public ProductResponse Handle(AcceptMaterialRequest command, IPlatform platform)
    {
        var request = platform.Query<LogisticRequest>().SingleOrDefault(r => r.NId == command.LogisticRequestNId);
        if (request is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownLogisticRequest, $"Logistic request {command.LogisticRequestNId} does not exist.");
        }

        var operation = platform.Create<TransportOperation>();
        operation.NId = command.TransportOperationNId!;
        operation.LogisticRequestNId = request.NId;
        operation.MaterialNId = request.MaterialNId;
        operation.Quantity = request.Quantity;
        operation.SourceBufferNId = request.SourceBufferNId;
        operation.DestinationBufferNId = request.DestinationBufferNId;
        operation.Status = TransportStatus.Released;
        platform.Submit(operation);
        return ProductResponse.Of(operation);
    }
}

internal static class TransportOperations
{
    public static ProductResponse? Find(IPlatform platform, string nid, string[] from, out TransportOperation operation)
    {
        operation = platform.Query<TransportOperation>().SingleOrDefault(t => t.NId == nid)!;
        if (operation is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownTransportOperation, $"Transport operation {nid} does not exist.");
        }

        return from.Contains(operation.Status)
            ? null
            : ProductResponse.Fail(ProductErrors.InvalidLogisticStatus, $"Transport operation {nid} is {operation.Status}.");
    }
}

[Command<ProductOperationBlock>]
public sealed class StartTransportOperation : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class StartTransportOperationHandler : ICommandHandler<StartTransportOperation, ProductResponse>
{
    public ProductResponse Handle(StartTransportOperation command, IPlatform platform)
    {
        if (TransportOperations.Find(platform, command.NId!, [TransportStatus.Released], out var operation) is { } failed)
        {
            return failed;
        }

        operation.Status = TransportStatus.Active;
        platform.Submit(operation);
        return ProductResponse.Of(operation);
    }
}

/// <summary>Hủy việc chở chưa ai nhận.</summary>
[Command<ProductOperationBlock>]
public sealed class CancelTransportOperation : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class CancelTransportOperationHandler : ICommandHandler<CancelTransportOperation, ProductResponse>
{
    public ProductResponse Handle(CancelTransportOperation command, IPlatform platform)
    {
        if (TransportOperations.Find(platform, command.NId!, [TransportStatus.Released], out var operation) is { } failed)
        {
            return failed;
        }

        operation.Status = TransportStatus.Cancelled;
        platform.Submit(operation);
        return ProductResponse.Of(operation);
    }
}

/// <summary>Hoàn tất: lô (nếu có) rời buffer nguồn, thuộc buffer đích và tới equipment của buffer đích.</summary>
[Command<ProductOperationBlock>]
public sealed class CompleteTransportOperation : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    public string? MtuNId { get; init; }
}

internal sealed class CompleteTransportOperationHandler : ICommandHandler<CompleteTransportOperation, ProductResponse>
{
    public ProductResponse Handle(CompleteTransportOperation command, IPlatform platform)
    {
        if (TransportOperations.Find(platform, command.NId!, [TransportStatus.Released, TransportStatus.Active], out var operation) is { } failed)
        {
            return failed;
        }

        if (command.MtuNId is not null)
        {
            if (Mtus.Find(platform, command.MtuNId, out var mtu) is { } missing)
            {
                return missing;
            }

            var target = platform.ProjectionQuery<Buffer>().Where(b => b.NId == operation.DestinationBufferNId).Select(b => b.EquipmentNId).Single();
            var assignment = platform.Query<BufferAssignment>().SingleOrDefault(a => a.NId == mtu.NId);
            if (assignment is null)
            {
                assignment = platform.Create<BufferAssignment>();
                assignment.NId = mtu.NId;
                assignment.MtuNId = mtu.NId;
            }

            assignment.BufferNId = operation.DestinationBufferNId;
            platform.Submit(assignment);

            var before = mtu.EquipmentNId;
            mtu.EquipmentNId = target;
            platform.Submit(mtu);
            History.Write(platform, mtu.NId, MtuHistoryActions.Edit, $"Equipment {before} -> {target} ({operation.NId})");
        }

        operation.Status = TransportStatus.Complete;
        platform.Submit(operation);
        return ProductResponse.Of(operation);
    }
}
