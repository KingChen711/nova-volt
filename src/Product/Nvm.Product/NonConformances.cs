using Nvm.Foundation;

namespace Nvm.Product;

/// <summary>Danh mục lỗi. Cờ tự hủy do phía dự án đọc khi tạo NC ở xưởng.</summary>
[Entity<ProductReferenceBlock>]
public sealed class Defect : Entity
{
    public string? Description { get; set; }

    public bool AutoScrap { get; set; }
}

/// <summary>Bản ghi "hàng này không đạt", gắn với một hay nhiều MTU.</summary>
[Entity<ProductOperationBlock>]
public sealed class NonConformance : Entity
{
    /// <summary>Một trong <see cref="NonConformanceStatus"/>.</summary>
    public string? Status { get; set; }

    public string? Type { get; set; }

    public string? Severity { get; set; }

    public string? DefectNId { get; set; }

    public string? WorkOrderNId { get; set; }

    public string? OperationNId { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    /// <summary>Có khi NC được kết luận (SCRAP hoặc USE AS IS).</summary>
    public DateTimeOffset? EndDate { get; set; }
}

/// <summary>MTU của một NC. NId = <c>&lt;NC&gt;|&lt;MTU&gt;</c>.</summary>
[Entity<ProductOperationBlock>]
public sealed class NonConformanceItem : Entity
{
    [Mandatory]
    public string? NonConformanceNId { get; set; }

    [Mandatory]
    public string? MtuNId { get; set; }
}

public static class NonConformanceStatus
{
    public const string Open = "OPEN";
    public const string Scrap = "SCRAP";
    public const string UseAsIs = "USE AS IS";
}

internal static class NonConformances
{
    /// <summary>MTU được thả về Available khi không còn NC OPEN nào; còn NC mở khác thì vẫn OnHold, không báo gì.</summary>
    public static void ReleaseIfNoOpenNc(IPlatform platform, Mtu mtu, string closingNc)
    {
        if (mtu.Status != MtuStatus.OnHold)
        {
            return;
        }

        var openNcs = platform.Query<NonConformance>().Where(n => n.Status == NonConformanceStatus.Open && n.NId != closingNc).Select(n => n.NId);
        var stillOpen = platform.Query<NonConformanceItem>().Any(i => i.MtuNId == mtu.NId && openNcs.Contains(i.NonConformanceNId!));
        if (!stillOpen)
        {
            mtu.Status = MtuStatus.Available;
            platform.Submit(mtu);
        }
    }
}

[Command<ProductReferenceBlock>]
public sealed class DefineDefect : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    public string? Description { get; init; }

    public bool AutoScrap { get; init; }
}

internal sealed class DefineDefectHandler : ICommandHandler<DefineDefect, ProductResponse>
{
    public ProductResponse Handle(DefineDefect command, IPlatform platform)
    {
        var defect = platform.Query<Defect>().SingleOrDefault(d => d.NId == command.NId) ?? platform.Create<Defect>();
        defect.NId = command.NId!;
        defect.Description = command.Description;
        defect.AutoScrap = command.AutoScrap;
        platform.Submit(defect);
        return ProductResponse.Of(defect);
    }
}

/// <summary>
/// Tạo NC cho một danh sách MTU. OPEN: MTU OnHold. SCRAP ngay lúc tạo (hủy hàng loạt): MTU Scrapped, NC có ngày kết
/// thúc, và NC OPEN đã có của MTU vẫn để mở. MTU đã Scrapped bị từ chối.
/// </summary>
[Command<ProductOperationBlock>]
public sealed class CreateNonConformance : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public IReadOnlyList<string>? MtuNIds { get; init; }

    /// <summary>Rỗng: OPEN.</summary>
    public string? Status { get; init; }

    public string? Type { get; init; }

    public string? Severity { get; init; }

    public string? DefectNId { get; init; }

    public string? WorkOrderNId { get; init; }

    public string? OperationNId { get; init; }
}

internal sealed class CreateNonConformanceHandler : ICommandHandler<CreateNonConformance, ProductResponse>
{
    public ProductResponse Handle(CreateNonConformance command, IPlatform platform)
    {
        var status = command.Status ?? NonConformanceStatus.Open;
        if (status is not (NonConformanceStatus.Open or NonConformanceStatus.Scrap))
        {
            return ProductResponse.Fail(ProductErrors.InvalidNonConformanceStatus, $"A non-conformance starts as {NonConformanceStatus.Open} or {NonConformanceStatus.Scrap}.");
        }

        if (platform.Query<NonConformance>().Any(n => n.NId == command.NId))
        {
            return ProductResponse.Fail(ProductErrors.DuplicateNId, $"Non-conformance {command.NId} already exists.");
        }

        var mtus = new List<Mtu>();
        foreach (var nid in command.MtuNIds!)
        {
            if (Mtus.Find(platform, nid, out var mtu) is { } missing)
            {
                return missing;
            }

            if (mtu.Status == MtuStatus.Scrapped)
            {
                return ProductResponse.Fail(ProductErrors.MtuScrapped, $"MTU {mtu.NId} is already scrapped.");
            }

            mtus.Add(mtu);
        }

        var now = platform.Time.GetUtcNow();
        var nc = platform.Create<NonConformance>();
        nc.NId = command.NId!;
        nc.Status = status;
        nc.Type = command.Type;
        nc.Severity = command.Severity;
        nc.DefectNId = command.DefectNId;
        nc.WorkOrderNId = command.WorkOrderNId;
        nc.OperationNId = command.OperationNId;
        nc.StartDate = now;
        nc.EndDate = status == NonConformanceStatus.Scrap ? now : null;
        platform.Submit(nc);

        foreach (var mtu in mtus)
        {
            var item = platform.Create<NonConformanceItem>();
            item.NId = nc.NId + "|" + mtu.NId;
            item.NonConformanceNId = nc.NId;
            item.MtuNId = mtu.NId;
            platform.Submit(item);

            mtu.Status = status == NonConformanceStatus.Scrap ? MtuStatus.Scrapped : MtuStatus.OnHold;
            platform.Submit(mtu);
        }

        return ProductResponse.Of(nc);
    }
}

/// <summary>Kết luận một NC OPEN: SCRAP (MTU Scrapped) hoặc USE AS IS (MTU Available nếu không còn NC OPEN khác).</summary>
[Command<ProductOperationBlock>]
public sealed class SetNonConformanceStatus : ICommand<ProductResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? Status { get; init; }
}

internal sealed class SetNonConformanceStatusHandler : ICommandHandler<SetNonConformanceStatus, ProductResponse>
{
    public ProductResponse Handle(SetNonConformanceStatus command, IPlatform platform)
    {
        var nc = platform.Query<NonConformance>().SingleOrDefault(n => n.NId == command.NId);
        if (nc is null)
        {
            return ProductResponse.Fail(ProductErrors.UnknownNonConformance, $"Non-conformance {command.NId} does not exist.");
        }

        if (nc.Status != NonConformanceStatus.Open || command.Status is not (NonConformanceStatus.Scrap or NonConformanceStatus.UseAsIs))
        {
            return ProductResponse.Fail(ProductErrors.InvalidNonConformanceStatus,
                $"Non-conformance {nc.NId} is {nc.Status}; only {NonConformanceStatus.Open} can change, to {NonConformanceStatus.Scrap} or {NonConformanceStatus.UseAsIs}.");
        }

        nc.Status = command.Status;
        nc.EndDate = platform.Time.GetUtcNow();
        platform.Submit(nc);

        var mtuNIds = platform.Query<NonConformanceItem>().Where(i => i.NonConformanceNId == nc.NId).Select(i => i.MtuNId).ToList();
        foreach (var mtu in platform.Query<Mtu>().Where(m => mtuNIds.Contains(m.NId)).ToList())
        {
            if (command.Status == NonConformanceStatus.Scrap)
            {
                mtu.Status = MtuStatus.Scrapped;
                platform.Submit(mtu);
            }
            else
            {
                NonConformances.ReleaseIfNoOpenNc(platform, mtu, nc.NId);
            }
        }

        return ProductResponse.Of(nc);
    }
}

/// <summary>Giữ một danh sách MTU (Available → OnHold) mà không tạo NC.</summary>
[Command<ProductOperationBlock>]
public sealed class HoldMtus : ICommand<ProductResponse>
{
    [Mandatory]
    public IReadOnlyList<string>? MtuNIds { get; init; }
}

internal sealed class HoldMtusHandler : ICommandHandler<HoldMtus, ProductResponse>
{
    public ProductResponse Handle(HoldMtus command, IPlatform platform)
    {
        foreach (var nid in command.MtuNIds!)
        {
            if (Mtus.Find(platform, nid, out var mtu) is { } missing)
            {
                return missing;
            }

            if (mtu.Status == MtuStatus.Scrapped)
            {
                return ProductResponse.Fail(ProductErrors.MtuScrapped, $"MTU {mtu.NId} is already scrapped.");
            }

            mtu.Status = MtuStatus.OnHold;
            platform.Submit(mtu);
        }

        return new ProductResponse();
    }
}

/// <summary>Thả một danh sách MTU (OnHold → Available). Một MTU còn NC OPEN hoặc đang bị khoanh thì cả lệnh bị từ chối.</summary>
[Command<ProductOperationBlock>]
public sealed class ReleaseMtus : ICommand<ProductResponse>
{
    [Mandatory]
    public IReadOnlyList<string>? MtuNIds { get; init; }
}

internal sealed class ReleaseMtusHandler : ICommandHandler<ReleaseMtus, ProductResponse>
{
    public ProductResponse Handle(ReleaseMtus command, IPlatform platform)
    {
        var openNcs = platform.Query<NonConformance>().Where(n => n.Status == NonConformanceStatus.Open).Select(n => n.NId);
        foreach (var nid in command.MtuNIds!)
        {
            if (Mtus.Find(platform, nid, out var mtu) is { } missing)
            {
                return missing;
            }

            if (mtu.InContainment || platform.Query<NonConformanceItem>().Any(i => i.MtuNId == nid && openNcs.Contains(i.NonConformanceNId!)))
            {
                return ProductResponse.Fail(ProductErrors.MtuNotReleasable, $"MTU {nid} has an open non-conformance or is in containment.");
            }

            if (mtu.Status == MtuStatus.OnHold)
            {
                mtu.Status = MtuStatus.Available;
                platform.Submit(mtu);
            }
        }

        return new ProductResponse();
    }
}
