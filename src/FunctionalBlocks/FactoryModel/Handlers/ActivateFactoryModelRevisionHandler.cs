using Nvm.Contracts.Events.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Commands;

namespace Nvm.FactoryModel.Handlers;

/// <summary>Puts a revision in force at a plant, and says what changed.</summary>
/// <param name="available">The model document currently on disk.</param>
/// <param name="active">What each plant is running.</param>
/// <param name="clock">The only clock (AGENTS.md K1).</param>
/// <remarks>
/// Holds the rules that need state, and nothing else. Shape checks happen a stage earlier in
/// <see cref="ActivateFactoryModelRevisionValidator"/>; deduplication and the audit entry are wrapped
/// around this call by the pipeline. What is left is the part only this Functional Block knows.
/// </remarks>
public sealed class ActivateFactoryModelRevisionHandler(
    FactoryModelSnapshot available,
    IActiveFactoryModel active,
    TimeProvider clock)
    : ICommandHandler<ActivateFactoryModelRevisionCommand, FactoryModelRevisionActivated>
{
    private readonly FactoryModelSnapshot _available = available;
    private readonly IActiveFactoryModel _active = active;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    /// <exception cref="FactoryModelActivationException">The revision cannot be put in force.</exception>
    public Task<FactoryModelRevisionActivated> HandleAsync(
        ActivateFactoryModelRevisionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var candidate = _available.FindSite(command.SiteId)
            ?? throw new FactoryModelActivationException(
                $"The model document does not describe plant '{command.SiteId}'.");

        // The caller names the revision it read. If the file has been replaced since, refusing is the
        // whole point: activating a document nobody looked at is how a decommissioned work cell turns
        // up on the shop floor again.
        if (_available.Revision != command.Revision)
        {
            throw new FactoryModelActivationException(
                $"Revision {command.Revision} was requested for '{command.SiteId}', "
                + $"but the model document on disk is revision {_available.Revision}.");
        }

        var current = _active.Current(command.SiteId);

        // Strictly greater, never equal. Re-activating the same revision would emit a second event
        // claiming a change that did not happen, and every consumer would rebuild its cache for
        // nothing. A repeat of the same intention is the pipeline's job, not this one's.
        if (current is not null && command.Revision <= current.Revision)
        {
            throw new FactoryModelActivationException(
                $"Plant '{command.SiteId}' is on revision {current.Revision}; "
                + $"revision {command.Revision} would not move it forward.");
        }

        var before = PathsOf(current?.Site);
        var after = PathsOf(candidate);

        // Compare-and-swap against the revision read above. The two checks before this one looked at
        // state that another activation may have moved since; without this, both would pass their
        // checks and both would write, and the plant would end up on whichever finished last while two
        // events each claimed to have moved it forward from the same revision.
        if (!_active.TryActivate(new ActiveFactoryModelRevision(command.Revision, candidate), current?.Revision))
        {
            throw new FactoryModelActivationException(
                $"Plant '{command.SiteId}' moved to another revision while revision {command.Revision} "
                + "was being activated. Read the current revision again and decide afresh.");
        }

        return Task.FromResult(new FactoryModelRevisionActivated(
            // Same value as the command's key. This is the join that lets deduplication at ingestion
            // and deduplication in the pipeline agree about what "the same thing" means; when the two
            // drift apart, each layer keys on something different and neither works.
            EventId: command.IdempotencyKey.Value,
            OccurredAt: _clock.GetUtcNow(),
            SiteId: candidate.SiteId,
            Revision: command.Revision,
            NodeCount: after.Count,
            EquipmentPathsAdded: Difference(after, before),
            EquipmentPathsRemoved: Difference(before, after)));
    }

    private static HashSet<string> PathsOf(FactorySite? site) =>
        site is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : [.. site.Root.Descend().Select(node => node.Path.Value)];

    // Sorted, so two runs over the same change produce byte-identical events. An event whose payload
    // depends on hash ordering cannot be compared against a golden file, and cannot be diffed by
    // whoever is trying to work out what a revision actually did.
    private static IReadOnlyList<string> Difference(HashSet<string> left, HashSet<string> right) =>
        [.. left.Except(right, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
