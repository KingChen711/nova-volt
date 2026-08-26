namespace Nvm.Contracts.Events;

/// <summary>
/// Declares the schema version of a domain event type. Required from v1, on every event.
/// </summary>
/// <remarks>
/// <para>
/// The version is part of the contract, not metadata about it: it ends the event type string
/// (<c>com.novavolt.traceability.unit-serialized.v1</c>) and the routing key
/// (<c>nvm.NV1.traceability.unit-serialized.v1</c>), and it is stored next to every row in the
/// event store so a reader knows which shape it is holding.
/// </para>
/// <para>When to bump, per docs/scope.md §7.4:</para>
/// <list type="bullet">
///   <item><description>Adding an optional field — do not bump. Old readers ignore it.</description></item>
///   <item><description>
///     Changing a field's meaning, removing it, or changing its type — bump, and write an upcaster
///     from the previous version. The golden file for the old version is never edited.
///   </description></item>
/// </list>
/// <para>
/// Applying it from v1 rather than "when we need it" is the whole point (AGENTS.md K6). By the time
/// a second version is needed, v1 events are already in the store and on the wire, and there is no
/// longer anywhere to add the marker they should have carried.
/// </para>
/// <para>
/// The attribute is not inherited. A derived event type is a different type with a different wire
/// name, so letting it pick up its base's number would produce two distinct payloads both claiming
/// to be v1 — and an upcaster chain that cannot tell them apart. Every event type states its own
/// version, even when it looks redundant.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [EventVersion(1)]
/// public sealed record FactoryModelRevisionActivated(Guid EventId, DateTimeOffset OccurredAt, string SiteId)
///     : IDomainEvent;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventVersionAttribute(int version) : Attribute
{
    /// <summary>The schema version, starting at 1.</summary>
    /// <remarks>
    /// Nothing is validated here on purpose. An attribute constructor that throws only fails when
    /// something reflects over the type — at runtime, in whichever service happens to touch it
    /// first, long after the mistake was committed. Analyzer NVM003 rejects a missing attribute and
    /// a version below 1 at build time instead, which is where a typo in a constant belongs.
    /// </remarks>
    public int Version { get; } = version;
}
