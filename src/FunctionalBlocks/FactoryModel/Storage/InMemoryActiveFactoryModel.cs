using System.Collections.Concurrent;

namespace Nvm.FactoryModel.Storage;

/// <summary>Keeps the active revision per plant in process memory.</summary>
/// <remarks>
/// Loses everything on restart, and two instances behind a load balancer would disagree about which
/// revision is in force. Both are unacceptable for a plant and both need a database — this exists so
/// the command pipeline has something real to act on before there is one.
/// </remarks>
public sealed class InMemoryActiveFactoryModel : IActiveFactoryModel
{
    private readonly ConcurrentDictionary<string, ActiveFactoryModelRevision> _active =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ActiveFactoryModelRevision? Current(string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        return _active.GetValueOrDefault(siteId);
    }

    /// <inheritdoc />
    public void Activate(ActiveFactoryModelRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        _active[revision.SiteId] = revision;
    }
}
