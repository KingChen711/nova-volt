using System.Globalization;
using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Tháo rã thành phẩm lỗi: thành phẩm phải có NC; đọc genealogy đệ quy để liệt kê thành phần; chỉ material trong danh sách
// được phép mới được tháo hoặc hủy; mọi lần tháo có nhật ký và hoàn tác được.

/// <summary>Material được phép tháo hoặc hủy khi tháo rã. NId = material.</summary>
[Entity<NvMasterBlock>]
public sealed class DisassemblyPart : Entity;

[Command<NvMasterBlock>]
public sealed class SetDisassemblyPart : ICommand<NvResponse>
{
    [Mandatory]
    public string? MaterialNId { get; init; }
}

internal sealed class SetDisassemblyPartHandler : ICommandHandler<SetDisassemblyPart, NvResponse>
{
    public NvResponse Handle(SetDisassemblyPart command, IPlatform platform)
    {
        if (!platform.ProjectionQuery<Material>().Any(m => m.NId == command.MaterialNId))
        {
            return NvResponse.Fail(NvErrors.UnknownMaterial, $"Material {command.MaterialNId} does not exist.");
        }

        var part = platform.Query<DisassemblyPart>().SingleOrDefault(p => p.NId == command.MaterialNId) ?? platform.Create<DisassemblyPart>();
        part.NId = command.MaterialNId!;
        platform.Submit(part);
        return NvResponse.Of(part);
    }
}

public static class DisassemblyActions
{
    /// <summary>Tháo ra: bỏ dòng genealogy giữa thành phần và cha của nó; thành phần dùng tiếp.</summary>
    public const string Remove = "Remove";

    /// <summary>Hủy: thành phần vào NC của thành phẩm và bị Scrapped.</summary>
    public const string Scrap = "Scrap";
}

/// <summary>Một lần tháo một thành phần. Hoàn tác dựa vào các cột ở đây.</summary>
[Entity<NvOperationBlock>]
public sealed class DisassemblyLog : Entity
{
    [Mandatory]
    public string? ProductMtuNId { get; set; }

    [Mandatory]
    public string? ComponentMtuNId { get; set; }

    public string? ParentMtuNId { get; set; }

    [Mandatory]
    public string? Action { get; set; }

    public string? NonConformanceNId { get; set; }

    public string? PreviousStatus { get; set; }

    public string? WorkOrderNId { get; set; }

    public string? OperationNId { get; set; }

    public decimal Quantity { get; set; }

    public bool Undone { get; set; }
}

public sealed class DisassemblyComponent
{
    public string? MtuNId { get; init; }

    public string? MaterialNId { get; init; }

    public string? ParentMtuNId { get; init; }

    public int Level { get; init; }

    public string? Status { get; init; }

    public bool Allowed { get; init; }
}

public sealed class DisassemblyResponse : CommandResponse
{
    public string? NonConformanceNId { get; set; }

    public IList<DisassemblyComponent> Components { get; } = [];

    public IList<string> Logs { get; } = [];
}

public sealed class DisassemblyChoice
{
    public string? MtuNId { get; init; }

    /// <summary><see cref="DisassemblyActions"/>.</summary>
    public string? Action { get; init; }
}

internal static class Disassemblies
{
    /// <summary>NC mới nhất của thành phẩm; không có thì màn báo "The NC doesn't exist".</summary>
    public static NonConformance? NonConformanceOf(IReadOnlyPlatform platform, string productMtuNId)
    {
        var ncs = platform.Query<NonConformanceItem>().Where(i => i.MtuNId == productMtuNId).Select(i => i.NonConformanceNId).ToList();
        return platform.Query<NonConformance>().Where(n => ncs.Contains(n.NId)).OrderByDescending(n => n.StartDate).FirstOrDefault();
    }

    public static List<DisassemblyComponent> Tree(IReadOnlyPlatform platform, string productMtuNId)
    {
        var allowed = platform.ProjectionQuery<DisassemblyPart>().Select(p => p.NId).ToHashSet(StringComparer.Ordinal);
        var upstream = QualityData.Upstream(platform, productMtuNId);
        var nids = upstream.Select(u => u.MtuNId).ToList();
        var mtus = platform.Query<Mtu>().Where(m => nids.Contains(m.NId)).ToDictionary(m => m.NId, StringComparer.Ordinal);
        return [.. upstream.Select(u => new DisassemblyComponent
        {
            MtuNId = u.MtuNId,
            ParentMtuNId = u.ParentNId,
            Level = u.Level,
            MaterialNId = mtus.GetValueOrDefault(u.MtuNId)?.MaterialNId,
            Status = mtus.GetValueOrDefault(u.MtuNId)?.Status,
            Allowed = allowed.Contains(mtus.GetValueOrDefault(u.MtuNId)?.MaterialNId ?? string.Empty),
        })];
    }
}

/// <summary>Cây thành phần của thành phẩm (đệ quy theo genealogy), đánh dấu thành phần nào được phép tháo. Không ghi gì.</summary>
[Command<NvOperationBlock>]
public sealed class ListDisassemblyComponents : ICommand<DisassemblyResponse>
{
    [Mandatory]
    public string? ProductMtuNId { get; init; }
}

internal sealed class ListDisassemblyComponentsHandler : ICommandHandler<ListDisassemblyComponents, DisassemblyResponse>
{
    public DisassemblyResponse Handle(ListDisassemblyComponents command, IPlatform platform)
    {
        var response = new DisassemblyResponse();
        if (Disassemblies.NonConformanceOf(platform, command.ProductMtuNId!) is not { } nc)
        {
            response.SetError(NvErrors.NoNonConformance, "The NC doesn't exist.");
            return response;
        }

        response.NonConformanceNId = nc.NId;
        foreach (var component in Disassemblies.Tree(platform, command.ProductMtuNId!))
        {
            response.Components.Add(component);
        }

        return response;
    }
}

[Command<NvOperationBlock>]
public sealed class DisassembleProduct : ICommand<DisassemblyResponse>
{
    [Mandatory]
    public string? ProductMtuNId { get; init; }

    [Mandatory]
    public IReadOnlyList<DisassemblyChoice>? Components { get; init; }
}

internal sealed class DisassembleProductHandler : ICommandHandler<DisassembleProduct, DisassemblyResponse>
{
    public DisassemblyResponse Handle(DisassembleProduct command, IPlatform platform)
    {
        var response = new DisassemblyResponse();
        if (Disassemblies.NonConformanceOf(platform, command.ProductMtuNId!) is not { } nc)
        {
            response.SetError(NvErrors.NoNonConformance, "The NC doesn't exist.");
            return response;
        }

        var tree = Disassemblies.Tree(platform, command.ProductMtuNId!).ToDictionary(c => c.MtuNId!, StringComparer.Ordinal);
        foreach (var choice in command.Components!)
        {
            if (choice.MtuNId is null || !tree.TryGetValue(choice.MtuNId, out var component))
            {
                response.SetError(NvErrors.NotAComponent, $"{choice.MtuNId} is not a component of {command.ProductMtuNId}.");
                return response;
            }

            if (!component.Allowed)
            {
                response.SetError(NvErrors.PartNotAllowed, $"Material {component.MaterialNId} is not allowed for disassembly.");
                return response;
            }

            if (choice.Action is not (DisassemblyActions.Remove or DisassemblyActions.Scrap))
            {
                response.SetError(NvErrors.InvalidDisposition, $"Action must be {DisassemblyActions.Remove} or {DisassemblyActions.Scrap}.");
                return response;
            }
        }

        response.NonConformanceNId = nc.NId;
        var index = platform.Query<DisassemblyLog>().Count(l => l.ProductMtuNId == command.ProductMtuNId);
        foreach (var choice in command.Components!)
        {
            var component = tree[choice.MtuNId!];
            var mtu = platform.Query<Mtu>().Single(m => m.NId == choice.MtuNId);
            var log = platform.Create<DisassemblyLog>();
            log.NId = string.Create(CultureInfo.InvariantCulture, $"DIS_{command.ProductMtuNId}_{++index:D3}");
            log.ProductMtuNId = command.ProductMtuNId;
            log.ComponentMtuNId = mtu.NId;
            log.ParentMtuNId = component.ParentMtuNId;
            log.Action = choice.Action;
            log.NonConformanceNId = nc.NId;
            log.PreviousStatus = mtu.Status;

            var links = platform.Query<MaterialConsumption>().Where(c => c.ProductMtuNId == component.ParentMtuNId && c.ComponentMtuNId == mtu.NId).ToList();
            if (choice.Action == DisassemblyActions.Remove)
            {
                log.WorkOrderNId = links.Select(l => l.WorkOrderNId).FirstOrDefault();
                log.OperationNId = links.Select(l => l.OperationNId).FirstOrDefault();
                log.Quantity = links.Sum(l => l.Quantity);
                foreach (var link in links)
                {
                    platform.Delete(link);
                }
            }
            else
            {
                var itemNId = nc.NId + "|" + mtu.NId;
                if (!platform.Query<NonConformanceItem>().Any(i => i.NId == itemNId))
                {
                    var item = platform.Create<NonConformanceItem>();
                    item.NId = itemNId;
                    item.NonConformanceNId = nc.NId;
                    item.MtuNId = mtu.NId;
                    platform.Submit(item);
                }

                mtu.Status = MtuStatus.Scrapped;
                platform.Submit(mtu);
            }

            platform.Submit(log);
            response.Logs.Add(log.NId);
        }

        return response;
    }
}

/// <summary>Hoàn tác một lần tháo: trả dòng genealogy, hoặc gỡ thành phần khỏi NC và trả trạng thái cũ.</summary>
[Command<NvOperationBlock>]
public sealed class UndoDisassembly : ICommand<NvResponse>
{
    [Mandatory]
    public string? LogNId { get; init; }
}

internal sealed class UndoDisassemblyHandler : ICommandHandler<UndoDisassembly, NvResponse>
{
    public NvResponse Handle(UndoDisassembly command, IPlatform platform)
    {
        var log = platform.Query<DisassemblyLog>().SingleOrDefault(l => l.NId == command.LogNId);
        if (log is null || log.Undone)
        {
            return NvResponse.Fail(NvErrors.UnknownDisassembly, $"Disassembly {command.LogNId} does not exist or is already undone.");
        }

        if (log.Action == DisassemblyActions.Remove)
        {
            var link = platform.Create<MaterialConsumption>();
            link.NId = link.Id.ToString();
            link.WorkOrderNId = log.WorkOrderNId;
            link.OperationNId = log.OperationNId;
            link.ProductMtuNId = log.ParentMtuNId;
            link.ComponentMtuNId = log.ComponentMtuNId;
            link.Quantity = log.Quantity;
            platform.Submit(link);
        }
        else
        {
            var itemNId = log.NonConformanceNId + "|" + log.ComponentMtuNId;
            if (platform.Query<NonConformanceItem>().SingleOrDefault(i => i.NId == itemNId) is { } item)
            {
                platform.Delete(item);
            }

            var mtu = platform.Query<Mtu>().Single(m => m.NId == log.ComponentMtuNId);
            mtu.Status = log.PreviousStatus;
            platform.Submit(mtu);
        }

        log.Undone = true;
        platform.Submit(log);
        return NvResponse.Of(log);
    }
}
