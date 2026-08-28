using System.Collections.Concurrent;
using System.Collections.Frozen;
using Nvm.FactoryModel.Entities;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Storage;

/// <summary>Answers <see cref="IEquipmentDirectory"/> from whatever revision each plant is running.</summary>
/// <remarks>
/// <para>
/// The lookup goes through <see cref="IActiveFactoryModel"/> rather than the catalog, and that is the
/// point of it: a message arriving from NV1 has to be read against the tree NV1 is running, even
/// while DE1 is still on an older revision. Asking the catalog for the newest document instead would
/// resolve devices that the plant sending them has not been rolled out to yet.
/// </para>
/// <para>
/// An index is built per plant and revision and kept. <see cref="FactorySite"/> carries a tree and no
/// index, so every lookup would otherwise walk it — forty nodes today, and once per message at five
/// thousand messages a second. Keyed on the revision as well as the plant so that an activation
/// invalidates nothing: it simply produces a key nothing has cached yet.
/// </para>
/// </remarks>
public sealed class FactoryModelEquipmentDirectory : IEquipmentDirectory
{
    private readonly IActiveFactoryModel _active;
    private readonly ConcurrentDictionary<(string SiteId, int Revision), FrozenDictionary<EquipmentPath, FactoryNode>> _indexes = new();

    /// <summary>Creates the directory over what each plant is currently running.</summary>
    /// <param name="active">The activation store.</param>
    public FactoryModelEquipmentDirectory(IActiveFactoryModel active)
    {
        ArgumentNullException.ThrowIfNull(active);

        _active = active;
    }

    /// <inheritdoc />
    public bool Contains(EquipmentPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return IndexFor(path.SiteId)?.ContainsKey(path) ?? false;
    }

    /// <inheritdoc />
    public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode)
    {
        ArgumentNullException.ThrowIfNull(line);

        var index = IndexFor(line.SiteId);

        if (index is null || string.IsNullOrEmpty(deviceCode))
        {
            return null;
        }

        // Built through TryParse rather than Append: the code came off a wire, so it can be anything,
        // and Append answers a malformed segment with an exception. A device calling itself something
        // impossible is a message to refuse, not a fault to throw out of a directory lookup.
        if (EquipmentPath.TryParse($"{line.Value}{EquipmentPath.Separator}{deviceCode}", out var direct)
            && index.ContainsKey(direct))
        {
            return direct;
        }

        if (!index.TryGetValue(line, out var lineNode))
        {
            return null;
        }

        foreach (var workCell in lineNode.Children)
        {
            if (EquipmentPath.TryParse($"{workCell.Path.Value}{EquipmentPath.Separator}{deviceCode}", out var nested)
                && index.ContainsKey(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private FrozenDictionary<EquipmentPath, FactoryNode>? IndexFor(string? siteId)
    {
        // Null for an enterprise-level path, which cannot name a device and cannot name a plant to
        // resolve one against. K3 falls out of this: a topic quoting a plant nobody has activated gets
        // no index, so nothing in it resolves.
        if (siteId is null)
        {
            return null;
        }

        var current = _active.Current(siteId);

        return current is null
            ? null
            : _indexes.GetOrAdd(
                (siteId, current.Revision),
                static (_, site) => site.Root.Descend().ToFrozenDictionary(node => node.Path),
                current.Site);
    }
}
