namespace Nvm.Contracts.Events.FactoryModel;

/// <summary>
/// A new revision of a site's ISA-95 factory model became the one in force.
/// </summary>
/// <param name="EventId">Identity of this occurrence. See <see cref="IDomainEvent.EventId"/>.</param>
/// <param name="OccurredAt">When the revision took effect. See <see cref="IDomainEvent.OccurredAt"/>.</param>
/// <param name="SiteId">The plant whose model changed, for example <c>NV1</c>.</param>
/// <param name="Revision">The revision now in force. Strictly greater than the previous one.</param>
/// <param name="NodeCount">How many nodes the tree holds at this revision.</param>
/// <param name="EquipmentPathsAdded">Equipment paths that exist at this revision and did not before.</param>
/// <param name="EquipmentPathsRemoved">
/// Equipment paths that existed at the previous revision and no longer do.
/// </param>
/// <remarks>
/// <para>
/// Plants change: a channel is added to a formation machine, a work cell goes out for a long
/// overhaul, a line is renamed for a new product. Each of those is a revision, and none of them
/// edits history — the model is versioned for the same reason the event store is append-only.
/// </para>
/// <para>
/// The removal list is the interesting half. A consumer holding a cached tree can apply additions
/// blindly, but a removal is a decision: work in progress may still be standing on the cell that
/// just left the model, and traceability records written last year still point at equipment paths
/// that no longer resolve. Neither of those is a data fault, and code that treats a missing path as
/// corruption will start rejecting valid history the first time a machine is decommissioned.
/// </para>
/// </remarks>
[EventVersion(1)]
public sealed record FactoryModelRevisionActivated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string SiteId,
    int Revision,
    int NodeCount,
    IReadOnlyList<string> EquipmentPathsAdded,
    IReadOnlyList<string> EquipmentPathsRemoved) : IDomainEvent;
