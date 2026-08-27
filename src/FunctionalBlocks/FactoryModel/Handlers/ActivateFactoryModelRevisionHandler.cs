using System.Collections.Immutable;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Commands;

namespace Nvm.FactoryModel.Handlers;

/// <summary>Puts a revision in force at a plant, and says what changed.</summary>
/// <param name="catalog">Every revision that exists, in force or not.</param>
/// <param name="active">What each plant is running.</param>
/// <param name="clock">The only clock (AGENTS.md K1).</param>
/// <remarks>
/// <para>
/// Holds the rules that need state, and nothing else. Shape checks happen a stage earlier in
/// <see cref="ActivateFactoryModelRevisionValidator"/>; deduplication and the audit entry are wrapped
/// around this call by the pipeline. What is left is the part only this Functional Block knows.
/// </para>
/// <para>
/// <b>Two documents, not one.</b> Moving a plant from revision 2 to revision 3 means reading both:
/// what it is running now and what it is being asked to run. The event carries the difference, so the
/// handler cannot work from a single "current document" — that shape can only ever announce a first
/// activation, and every path would look added forever.
/// </para>
/// </remarks>
public sealed class ActivateFactoryModelRevisionHandler(
    IFactoryModelCatalog catalog,
    IActiveFactoryModel active,
    TimeProvider clock)
    : ICommandHandler<ActivateFactoryModelRevisionCommand, FactoryModelRevisionActivated>
{
    private readonly IFactoryModelCatalog _catalog = catalog;
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

        // The caller names the revision it read, and the catalog either holds that document or it does
        // not. Documents are never rewritten, so a revision that resolves here is the same tree the
        // caller looked at — which is what the old "does the file still say what you think" check was
        // reaching for, without being able to prove it.
        var document = _catalog.Find(command.Revision)
            ?? throw new FactoryModelActivationException(
                $"The catalog does not hold revision {command.Revision}. "
                + $"It holds: {string.Join(", ", _catalog.Revisions)}.");

        var candidate = document.FindSite(command.SiteId)
            ?? throw new FactoryModelActivationException(
                $"Revision {command.Revision} does not describe plant '{command.SiteId}'.");

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
    //
    // ImmutableArray rather than letting the compiler pick a read-only list for the IReadOnlyList the
    // contract declares. Both are safe today; only one stays safe if somebody later assigns a plain
    // List here, because then the guarantee comes from the type instead of from how the value was
    // built at this one call site.
    private static ImmutableArray<string> Difference(HashSet<string> left, HashSet<string> right) =>
        [.. left.Except(right, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
