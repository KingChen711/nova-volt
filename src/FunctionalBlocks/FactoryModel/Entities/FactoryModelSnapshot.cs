using System.Collections.Frozen;
using System.Collections.Immutable;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Entities;

/// <summary>One revision of the factory model: the tree, the sites, and a flat index over both.</summary>
/// <remarks>
/// <para>
/// Immutable. A change to the plant produces a new snapshot at a higher revision rather than an edit
/// to this one, which is what lets a question about last March be answered with the model that was in
/// force last March.
/// </para>
/// <para>
/// Two views of the same data, because two very different questions get asked. Walking the tree
/// answers "what is under this area"; the flat index answers "what is
/// <c>NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142</c>" — which is the one asked on every single
/// message arriving from the shop floor, and therefore the one that has to be a dictionary lookup
/// rather than a walk.
/// </para>
/// </remarks>
public sealed class FactoryModelSnapshot
{
    private readonly FrozenDictionary<EquipmentPath, FactoryNode> _index;

    internal FactoryModelSnapshot(
        int revision,
        DateTimeOffset generatedAt,
        FactoryNode root,
        IEnumerable<FactorySite> sites)
    {
        Revision = revision;
        GeneratedAt = generatedAt;
        Root = root;
        Sites = sites.ToImmutableArray();

        // Frozen rather than a plain dictionary: built once at load, read on every message off the
        // shop floor, and never written again. It also removes the last way the index could drift
        // from the tree — there is no mutator to reach, by cast or otherwise.
        _index = root.Descend().ToFrozenDictionary(node => node.Path);
    }

    /// <summary>Which revision of the plant this is. Strictly increasing over time.</summary>
    public int Revision { get; }

    /// <summary>When the source file was produced.</summary>
    public DateTimeOffset GeneratedAt { get; }

    /// <summary>The enterprise node, and through it the whole tree.</summary>
    public FactoryNode Root { get; }

    /// <summary>The plants in this revision.</summary>
    public ImmutableArray<FactorySite> Sites { get; }

    /// <summary>How many nodes the tree holds, at every level together.</summary>
    public int NodeCount => _index.Count;

    /// <summary>Every path in this revision.</summary>
    public ImmutableArray<EquipmentPath> Paths => _index.Keys;

    /// <summary>
    /// Finds a node by its path, or returns null when this revision does not contain it.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, and the distinction matters more than it looks. A path that is
    /// absent is the normal case when reading history: a work cell decommissioned last year is still
    /// named by every traceability record written while it existed. Treating that as an error would
    /// make the system reject valid history the first time a machine is scrapped.
    /// </remarks>
    public FactoryNode? Find(EquipmentPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return _index.GetValueOrDefault(path);
    }

    /// <summary>Finds a node by its path written as text.</summary>
    /// <returns>The node, or null when the path is unknown <b>or malformed</b>.</returns>
    public FactoryNode? Find(string? path) =>
        EquipmentPath.TryParse(path, out var parsed) ? Find(parsed) : null;

    /// <summary>Finds a plant by its code.</summary>
    public FactorySite? FindSite(string? siteId) =>
        Sites.FirstOrDefault(site => string.Equals(site.SiteId, siteId, StringComparison.Ordinal));
}
