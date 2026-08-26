using Nvm.Contracts.Events;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// A domain event wrapped in a CloudEvents 1.0 envelope, as defined in docs/scope.md §7.4.
/// </summary>
/// <typeparam name="TData">The domain event carried in <see cref="Data"/>.</typeparam>
/// <remarks>
/// <para>
/// This envelope is the canonical form of an event: it is what the event store persists, what a
/// published passport carries, and what an auditor is eventually shown. It is deliberately not the
/// same thing as whatever a message library wraps around a payload to do its own routing and retry —
/// that belongs to the transport and may be replaced; this shape may not.
/// </para>
/// <para>
/// Serialization lives in a separate commit. Property names here follow C# conventions; the wire
/// attribute names are all lower case with no separators (<c>specversion</c>, <c>datacontenttype</c>,
/// <c>dataschema</c>), which is a mapping, not a naming policy.
/// </para>
/// </remarks>
public sealed record CloudEventEnvelope<TData>
    where TData : IDomainEvent
{
    /// <summary>The only CloudEvents specification version this system emits.</summary>
    public const string SpecVersionValue = "1.0";

    /// <summary>The only payload encoding this envelope carries.</summary>
    /// <remarks>
    /// Sparkplug B arrives as protobuf, but it is decoded at the edge and only becomes a domain event
    /// afterwards (M2). Nothing that reaches this envelope is anything but JSON.
    /// </remarks>
    public const string JsonContentType = "application/json";

    /// <summary>The domain event itself — the <c>data</c> attribute.</summary>
    public required TData Data { get; init; }

    /// <summary>What happened, and which schema version says so.</summary>
    public required EventTypeName Type { get; init; }

    /// <summary>Which deployable, at which site, is asserting this.</summary>
    public required EventSource Source { get; init; }

    /// <summary>CloudEvents <c>specversion</c>. Constant by design.</summary>
    public string SpecVersion => SpecVersionValue;

    /// <summary>
    /// CloudEvents <c>id</c>. Read straight off the payload rather than stored separately.
    /// </summary>
    /// <remarks>
    /// Two places holding the same identity is two places that can disagree, and the one time it
    /// matters is during deduplication — when a mismatch means the same fact is counted twice.
    /// Deriving it makes the disagreement unrepresentable instead of merely discouraged.
    /// </remarks>
    public Guid Id => Data.EventId;

    /// <summary>CloudEvents <c>time</c>. Derived from the payload, for the same reason as <see cref="Id"/>.</summary>
    public DateTimeOffset Time => Data.OccurredAt;

    /// <summary>CloudEvents <c>datacontenttype</c>.</summary>
    public string DataContentType => JsonContentType;

    /// <summary>
    /// What the event is about, as a URN — for example <c>urn:trace-unit:cell:NV1CL16238A00123</c>.
    /// </summary>
    /// <remarks>
    /// Optional in the specification and left as a string here on purpose. Building it needs to know
    /// about serial numbers and unit kinds, which live one layer up; teaching this project about them
    /// would point the dependency arrow backwards.
    /// </remarks>
    public string? Subject { get; init; }

    /// <summary>Where the schema for <see cref="Data"/> is published, when it is published.</summary>
    public Uri? DataSchema { get; init; }

    /// <summary>
    /// The business thread this event belongs to, typically a work order such as <c>WO-2026-0042</c>.
    /// </summary>
    /// <remarks>
    /// Shared by every event in the same unit of work, which is what makes it possible to ask "show
    /// me everything that happened for this order" across services that never call each other.
    /// </remarks>
    public string? CorrelationId { get; init; }

    /// <summary>What directly caused this event, typically the operation run or the command.</summary>
    /// <remarks>
    /// Correlation groups; causation orders. Together they reconstruct the chain that led here — the
    /// question an investigation actually asks after a defect is found.
    /// </remarks>
    public string? CausationId { get; init; }

    /// <summary>
    /// The value events must stay in order relative to, normally the unit id.
    /// </summary>
    /// <remarks>
    /// Ordering is only ever guaranteed within one partition key, never globally. Two events about
    /// the same cell must not overtake each other; two events about different cells may, and
    /// pretending otherwise is what turns a parallel consumer into a serial one.
    /// </remarks>
    public string? PartitionKey { get; init; }
}
