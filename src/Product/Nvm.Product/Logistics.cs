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

/// <summary>Yêu cầu chở một MTU từ equipment này tới equipment khác, qua các chặng trong luồng.</summary>
[Entity<ProductOperationBlock>]
public sealed class LogisticRequest : Entity
{
    [Mandatory]
    public string? MtuNId { get; set; }

    public string? FromEquipmentNId { get; set; }

    [Mandatory]
    public string? ToEquipmentNId { get; set; }

    /// <summary>Created, Released, InTransit hoặc Completed.</summary>
    public string? Status { get; set; }
}

public static class LogisticStatus
{
    public const string Created = "Created";
    public const string Released = "Released";
    public const string InTransit = "InTransit";
    public const string Completed = "Completed";
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

[Command<ProductOperationBlock>]
public sealed class CreateLogisticRequest : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? MtuNId { get; init; }

    [Mandatory]
    public string? ToEquipmentNId { get; init; }
}

internal sealed class CreateLogisticRequestHandler : ICommandHandler<CreateLogisticRequest, ProductResponse>
{
    public ProductResponse Handle(CreateLogisticRequest command, IPlatform platform)
    {
        if (Mtus.Find(platform, command.MtuNId!, out var mtu) is { } missing)
        {
            return missing;
        }

        if (!platform.ProjectionQuery<Equipment>().Any(e => e.NId == command.ToEquipmentNId))
        {
            return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {command.ToEquipmentNId} does not exist.");
        }

        var request = platform.Create<LogisticRequest>();
        request.NId = command.NId!;
        request.MtuNId = mtu.NId;
        request.FromEquipmentNId = mtu.EquipmentNId;
        request.ToEquipmentNId = command.ToEquipmentNId;
        request.Status = LogisticStatus.Created;
        platform.Submit(request);
        return ProductResponse.Of(request);
    }
}

internal static class LogisticRequests
{
    /// <summary>Đổi trạng thái nếu đang ở một trong <paramref name="from"/>; tùy chọn chuyển MTU tới equipment.</summary>
    public static ProductResponse Advance(IPlatform platform, string nid, string to, string[] from, string? moveTo)
    {
        var request = platform.Query<LogisticRequest>().SingleOrDefault(r => r.NId == nid);
        if (request is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownLogisticRequest, $"Logistic request {nid} does not exist.");
        }

        if (!from.Contains(request.Status))
        {
            return ProductResponse.Fail(ProductErrors.InvalidLogisticStatus, $"Logistic request {nid} is {request.Status}, cannot go to {to}.");
        }

        if (moveTo is not null)
        {
            if (!platform.ProjectionQuery<Equipment>().Any(e => e.NId == moveTo))
            {
                return ProductResponse.Fail(ProductErrors.UnknownEquipment, $"Equipment {moveTo} does not exist.");
            }

            if (Mtus.Find(platform, request.MtuNId!, out var mtu) is { } missing)
            {
                return missing;
            }

            var before = mtu.EquipmentNId;
            mtu.EquipmentNId = moveTo;
            platform.Submit(mtu);
            History.Write(platform, mtu.NId, MtuHistoryActions.Edit, $"Equipment {before} -> {moveTo} ({request.NId})");
        }

        request.Status = to;
        platform.Submit(request);
        return ProductResponse.Of(request);
    }
}

[Command<ProductOperationBlock>]
public sealed class ReleaseLogisticRequest : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class ReleaseLogisticRequestHandler : ICommandHandler<ReleaseLogisticRequest, ProductResponse>
{
    public ProductResponse Handle(ReleaseLogisticRequest command, IPlatform platform) =>
        LogisticRequests.Advance(platform, command.NId!, LogisticStatus.Released, [LogisticStatus.Created], moveTo: null);
}

/// <summary>Chở MTU qua một chặng trong luồng: MTU tới equipment trung gian, yêu cầu ở InTransit.</summary>
[Command<ProductOperationBlock>]
public sealed class MoveLogisticRequestWithinFlow : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? EquipmentNId { get; init; }
}

internal sealed class MoveLogisticRequestWithinFlowHandler : ICommandHandler<MoveLogisticRequestWithinFlow, ProductResponse>
{
    public ProductResponse Handle(MoveLogisticRequestWithinFlow command, IPlatform platform) =>
        LogisticRequests.Advance(platform, command.NId!, LogisticStatus.InTransit, [LogisticStatus.Released, LogisticStatus.InTransit],
            command.EquipmentNId);
}

/// <summary>Hoàn tất: MTU tới equipment đích của yêu cầu.</summary>
[Command<ProductOperationBlock>]
public sealed class CompleteLogisticRequest : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class CompleteLogisticRequestHandler : ICommandHandler<CompleteLogisticRequest, ProductResponse>
{
    public ProductResponse Handle(CompleteLogisticRequest command, IPlatform platform)
    {
        var target = platform.Query<LogisticRequest>().Where(r => r.NId == command.NId).Select(r => r.ToEquipmentNId).SingleOrDefault();
        return LogisticRequests.Advance(platform, command.NId!, LogisticStatus.Completed, [LogisticStatus.Released, LogisticStatus.InTransit],
            target ?? string.Empty);
    }
}
