using System.Globalization;
using Nvm.Foundation;

namespace Nvm.Product;

/// <summary>
/// Yêu cầu khoanh một nhóm hàng nghi lỗi, nhận từ hệ thống truy vết chất lượng bên ngoài kèm danh sách MTU.
/// MOM không tự tìm hàng liên quan.
/// </summary>
[Entity<ProductOperationBlock>]
public sealed class ContainmentRequest : Entity
{
    /// <summary>Tên do hệ gửi đặt; phía dự án đọc mã trong tên để đặt tên riêng.</summary>
    public string? Name { get; set; }

    public string? SourceSystem { get; set; }

    /// <summary>Editing (không có MTU), Ready, Released hoặc Closed.</summary>
    public string? Status { get; set; }

    public DateTimeOffset? ImportedOn { get; set; }
}

/// <summary>MTU của một containment request. NId = <c>&lt;CR&gt;|&lt;MTU&gt;</c>.</summary>
[Entity<ProductOperationBlock>]
public sealed class ContainmentItem : Entity
{
    [Mandatory]
    public string? ContainmentRequestNId { get; set; }

    [Mandatory]
    public string? MtuNId { get; set; }
}

public static class ContainmentStatus
{
    public const string Editing = "Editing";
    public const string Ready = "Ready";
    public const string Released = "Released";
    public const string Closed = "Closed";
}

/// <summary>Phát lúc nhập, sau commit: phía dự án nghe để đặt tên theo mã và ghi màn theo dõi.</summary>
[Event<ProductOperationBlock>]
[EventVersion(1)]
public sealed record ContainmentRequestImported(Guid ContainmentRequestId, string NId, string? Name, string? SourceSystem, int MtuCount)
    : IFoundationEvent;

/// <summary>
/// Nhập containment request: cấp số <c>ContainmentRequest_&lt;n&gt;</c>, khoanh từng MTU (Available → OnHold, cờ In
/// Containment; MTU đã Scrapped giữ Scrapped). Có MTU thì Ready, không có thì Editing.
/// </summary>
[Command<ProductOperationBlock>]
public sealed class ImportContainmentRequest : ICommand<ProductResponse>
{
    public string? Name { get; init; }

    public string? SourceSystem { get; init; }

    public IReadOnlyList<string>? MtuNIds { get; init; }
}

internal sealed class ImportContainmentRequestHandler : ICommandHandler<ImportContainmentRequest, ProductResponse>
{
    private const string Prefix = "ContainmentRequest_";

    public ProductResponse Handle(ImportContainmentRequest command, IPlatform platform)
    {
        var mtus = new List<Mtu>();
        foreach (var nid in command.MtuNIds ?? [])
        {
            if (Mtus.Find(platform, nid, out var mtu) is { } missing)
            {
                return missing;
            }

            mtus.Add(mtu);
        }

        var used = platform.Query<ContainmentRequest>().Count(c => c.NId.StartsWith(Prefix));
        var request = platform.Create<ContainmentRequest>();
        request.NId = Prefix + (used + 1).ToString("D6", CultureInfo.InvariantCulture);
        request.Name = command.Name;
        request.SourceSystem = command.SourceSystem;
        request.Status = mtus.Count > 0 ? ContainmentStatus.Ready : ContainmentStatus.Editing;
        request.ImportedOn = platform.Time.GetUtcNow();
        platform.Submit(request);

        foreach (var mtu in mtus)
        {
            var item = platform.Create<ContainmentItem>();
            item.NId = request.NId + "|" + mtu.NId;
            item.ContainmentRequestNId = request.NId;
            item.MtuNId = mtu.NId;
            platform.Submit(item);

            mtu.InContainment = true;
            if (mtu.Status == MtuStatus.Available)
            {
                mtu.Status = MtuStatus.OnHold;
            }

            platform.Submit(mtu);
        }

        platform.RaiseEventOnCommit(new ContainmentRequestImported(request.Id, request.NId, request.Name, request.SourceSystem, mtus.Count));
        return ProductResponse.Of(request);
    }
}

internal static class Containments
{
    public static ProductResponse? Find(IPlatform platform, string nid, out ContainmentRequest request)
    {
        request = platform.Query<ContainmentRequest>().SingleOrDefault(c => c.NId == nid)!;
        return request is null
            ? ProductResponse.Fail(ProductErrors.UnknownContainmentRequest, $"Containment request {nid} does not exist.")
            : null;
    }
}

/// <summary>Nhả cả nhóm: bỏ cờ In Containment, MTU OnHold về Available nếu không còn NC OPEN. CR Closed không nhả được.</summary>
[Command<ProductOperationBlock>]
public sealed class ReleaseContainmentRequest : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class ReleaseContainmentRequestHandler : ICommandHandler<ReleaseContainmentRequest, ProductResponse>
{
    public ProductResponse Handle(ReleaseContainmentRequest command, IPlatform platform)
    {
        if (Containments.Find(platform, command.NId!, out var request) is { } missing)
        {
            return missing;
        }

        if (request.Status is ContainmentStatus.Closed or ContainmentStatus.Released)
        {
            return ProductResponse.Fail(ProductErrors.ContainmentClosed, $"Containment request {request.NId} is {request.Status}.");
        }

        var mtuNIds = platform.Query<ContainmentItem>().Where(i => i.ContainmentRequestNId == request.NId).Select(i => i.MtuNId).ToList();
        foreach (var mtu in platform.Query<Mtu>().Where(m => mtuNIds.Contains(m.NId)).ToList())
        {
            mtu.InContainment = false;
            platform.Submit(mtu);
            NonConformances.ReleaseIfNoOpenNc(platform, mtu, closingNc: string.Empty);
        }

        request.Status = ContainmentStatus.Released;
        platform.Submit(request);
        return ProductResponse.Of(request);
    }
}

/// <summary>Đóng CR. Không nhả hàng: MTU vẫn OnHold và In Containment.</summary>
[Command<ProductOperationBlock>]
public sealed class CloseContainmentRequest : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class CloseContainmentRequestHandler : ICommandHandler<CloseContainmentRequest, ProductResponse>
{
    public ProductResponse Handle(CloseContainmentRequest command, IPlatform platform)
    {
        if (Containments.Find(platform, command.NId!, out var request) is { } missing)
        {
            return missing;
        }

        request.Status = ContainmentStatus.Closed;
        platform.Submit(request);
        return ProductResponse.Of(request);
    }
}
