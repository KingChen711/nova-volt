using System.Collections.Concurrent;

namespace Nvm.FactoryModel.Storage;

/// <summary>Keeps the active revision per plant in process memory.</summary>
/// <remarks>
/// <para>
/// Loses everything on restart, and two instances behind a load balancer would disagree about which
/// revision is in force. Both are unacceptable for a plant and both need a database — this exists so
/// the command pipeline has something real to act on before there is one.
/// </para>
/// <para>
/// Within one process the compare-and-swap is real, so two activations racing for the same plant end
/// with exactly one in force and the loser told it lost. That much survives the move to a database;
/// only the durability has to be rebuilt there.
/// </para>
/// </remarks>
public sealed class InMemoryActiveFactoryModel : IActiveFactoryModel
{
    private readonly ConcurrentDictionary<string, ActiveFactoryModelRevision> _active =
        new(StringComparer.Ordinal);

    // Guards the read-compare-write in TryActivate only. Reads stay outside it, which is what the
    // concurrent dictionary is for: an operator opening the model viewer must not queue behind a
    // rollout.
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ActiveFactoryModelRevision? Current(string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        return _active.GetValueOrDefault(siteId);
    }

    /// <inheritdoc />
    public bool TryActivate(ActiveFactoryModelRevision revision, int? expectedCurrentRevision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        lock (_gate)
        {
            // Null compares equal to null, which is exactly the first-activation case: the caller saw
            // no revision in force and is asking that there still be none.
            if (_active.GetValueOrDefault(revision.SiteId)?.Revision != expectedCurrentRevision)
            {
                return false;
            }

            _active[revision.SiteId] = revision;

            return true;
        }
    }
}
