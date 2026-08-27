using System.Collections.Immutable;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Entities;

/// <summary>One node of a site's ISA-95 tree, together with everything under it.</summary>
/// <remarks>
/// <para>
/// The tree's shape is carried by <see cref="EquipmentPath"/> rather than by a parent reference or a
/// table of permitted parent levels. That is what makes "equipment hanging directly off a site"
/// unrepresentable instead of merely forbidden: a child's path extends its parent's by one segment,
/// the level is read off the depth, so a child of a site is an area and can be nothing else.
/// </para>
/// <para>
/// Read-only. Changing the plant means activating a new revision, not editing a node in place — for
/// the same reason the event store is append-only. Traceability records written last year point at
/// paths that may no longer exist, and they still have to resolve to what they meant then.
/// </para>
/// </remarks>
public sealed record FactoryNode
{
    private FactoryNode(EquipmentPath path, string name, ImmutableArray<FactoryNode> children)
    {
        Path = path;
        Name = name;
        Children = children;
    }

    /// <summary>Where this node sits in the hierarchy.</summary>
    public EquipmentPath Path { get; }

    /// <summary>What people call it, for screens and reports. Never an identifier.</summary>
    public string Name { get; }

    /// <summary>The nodes one level down, in the order the model declares them.</summary>
    /// <remarks>
    /// <see cref="ImmutableArray{T}"/>, because <c>IReadOnlyList</c> is a promise about one reference
    /// and not about the object behind it: hand back the caller's <c>List</c> through it and the
    /// caller can still add to it, or anyone can cast it back and do the same. A node gained or lost
    /// after <see cref="FactoryModelSnapshot"/> built its flat index puts the tree and the index into
    /// permanent disagreement — the walk finds a machine the lookup denies exists — and a lookup is
    /// what every message off the shop floor uses.
    /// </remarks>
    public ImmutableArray<FactoryNode> Children { get; }

    /// <summary>The node's own code, the last segment of its path.</summary>
    public string Code => Path.Code;

    /// <summary>Which level of the hierarchy this is.</summary>
    public FactoryNodeKind Kind => Path.Kind;

    /// <summary>
    /// The plant this node belongs to, or null for the enterprise itself.
    /// </summary>
    /// <remarks>
    /// Derived from the path rather than stored, so a node cannot claim to be in one plant while
    /// sitting in another's subtree. AGENTS.md K3 asks that every record carry a site; the enterprise
    /// is the one level where that question has no answer, and it is answered with null rather than
    /// with an empty string that would sort and compare like a real site code.
    /// </remarks>
    public string? SiteId => Path.SiteId;

    /// <summary>Builds a node and checks that its children really are its children.</summary>
    /// <param name="path">Where the node sits.</param>
    /// <param name="name">Display name.</param>
    /// <param name="children">Nodes one level down, or null for a leaf. Copied, not kept.</param>
    /// <exception cref="ArgumentException">
    /// A child's path is not this node's path extended by exactly one segment, or two children share a
    /// code.
    /// </exception>
    public static FactoryNode Create(EquipmentPath path, string name, IEnumerable<FactoryNode>? children = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Copied here, before anything is checked. Validating the caller's collection and then storing
        // that same collection would check one thing and keep another: the caller still holds it and
        // can add an unvalidated node the moment this returns.
        var declared = children is null ? [] : children.ToImmutableArray();

        foreach (var child in declared)
        {
            if (child.Path.Parent != path)
            {
                throw new ArgumentException(
                    $"'{child.Path}' is not a child of '{path}'. A child's path is its parent's plus one segment.",
                    nameof(children));
            }
        }

        // Two machines with the same code in one cell would give the flat lookup two answers for one
        // path, and whichever the index happened to keep would be the one traceability believed.
        var codes = declared.Select(child => child.Code).ToArray();

        if (codes.Length != codes.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException($"'{path}' has two children with the same code.", nameof(children));
        }

        return new FactoryNode(path, name, declared);
    }

    /// <summary>Walks this node and everything beneath it, parents before children.</summary>
    public IEnumerable<FactoryNode> Descend()
    {
        yield return this;

        foreach (var descendant in Children.SelectMany(child => child.Descend()))
        {
            yield return descendant;
        }
    }
}
