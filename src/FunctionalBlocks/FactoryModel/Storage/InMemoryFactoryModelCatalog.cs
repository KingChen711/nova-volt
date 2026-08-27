using System.Collections.Immutable;
using Nvm.FactoryModel.Entities;

namespace Nvm.FactoryModel.Storage;

/// <summary>Holds every revision in process memory, read once at startup.</summary>
/// <remarks>
/// <para>
/// Unlike <see cref="InMemoryActiveFactoryModel"/>, losing this on restart costs nothing: the
/// documents are on disk and reading them again produces the same catalog. What is <i>not</i> durable
/// is which revision each plant had in force — and that asymmetry is deliberate, because it is
/// exactly the line M5 has to move. See the restart test in <c>ActivateFactoryModelRevisionTests</c>,
/// which pins the limit rather than leaving it as a comment.
/// </para>
/// <para>
/// Immutable after construction. A revision that could be replaced while the process runs would
/// reintroduce the problem the catalog exists to remove.
/// </para>
/// </remarks>
public sealed class InMemoryFactoryModelCatalog : IFactoryModelCatalog
{
    private readonly Dictionary<int, FactoryModelSnapshot> _byRevision;

    /// <summary>Builds a catalog from documents that have already been read and validated.</summary>
    /// <param name="revisions">The documents, in any order.</param>
    /// <exception cref="ArgumentException">
    /// No documents at all, or two documents claiming the same revision. Both mean the caller assembled
    /// the catalog wrongly; a plant with no model cannot be served, and two documents at one revision
    /// means the answer to "what was in force" depends on load order.
    /// </exception>
    public InMemoryFactoryModelCatalog(IEnumerable<FactoryModelSnapshot> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);

        _byRevision = [];

        foreach (var snapshot in revisions)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (!_byRevision.TryAdd(snapshot.Revision, snapshot))
            {
                throw new ArgumentException(
                    $"Two documents claim revision {snapshot.Revision}.",
                    nameof(revisions));
            }
        }

        if (_byRevision.Count == 0)
        {
            throw new ArgumentException("A catalog needs at least one revision.", nameof(revisions));
        }

        Revisions = [.. _byRevision.Keys.Order()];
    }

    /// <inheritdoc />
    public ImmutableArray<int> Revisions { get; }

    /// <inheritdoc />
    public int LatestRevision => Revisions[^1];

    /// <inheritdoc />
    public FactoryModelSnapshot? Find(int revision) => _byRevision.GetValueOrDefault(revision);
}
