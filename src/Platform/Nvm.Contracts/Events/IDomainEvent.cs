namespace Nvm.Contracts.Events;

/// <summary>
/// A fact that already happened on the shop floor and that the system has accepted as true.
/// </summary>
/// <remarks>
/// <para>
/// Domain events are named in the past tense and in the language of the plant, not of the code:
/// <c>ProductionUnitSerialized</c>, <c>UnitQuarantined</c>, <c>FormationRunCompleted</c>. The full
/// catalogue lives in docs/scope.md §6.5.
/// </para>
/// <para>
/// A domain event is not the same thing as telemetry. If it changes the business state of a
/// production unit it is an event and belongs in the event store; if it is continuous observation
/// — a dryer temperature every 100 ms — it is telemetry and belongs in TimescaleDB. The boundary
/// is docs/scope.md §5.5, and getting it wrong is how event stores turn into time-series databases
/// that nobody can replay.
/// </para>
/// <para>
/// Implementations are records. They carry no behaviour, no references to entities, and nothing
/// that cannot survive a round trip through JSON: an event read back in 2036 has only its own
/// fields to work with.
/// </para>
/// <para>
/// There is deliberately no AggregateId here. Aggregates arrive in M5, and a field that nobody can
/// fill correctly yet is a field that gets filled carelessly. It will be added by the layer that
/// owns aggregates, not by this marker.
/// </para>
/// </remarks>
public interface IDomainEvent
{
    /// <summary>
    /// Identity of this one occurrence, used to recognise it when it arrives twice.
    /// </summary>
    /// <remarks>
    /// The bus is at-least-once, so the same event will be delivered more than once and every
    /// handler has to cope (AGENTS.md K7). This value becomes the CloudEvents <c>id</c> attribute
    /// on the wire, and for an event produced by a command it must equal that command's
    /// idempotency key — the deterministic UUIDv5 built from the natural key in
    /// docs/scope.md §7.2. When the two drift apart, deduplication at ingestion and deduplication
    /// at the command handler start keying on different values and neither one works.
    /// </remarks>
    Guid EventId { get; }

    /// <summary>
    /// When the system recorded this fact, always with an explicit UTC offset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <c>recorded_at</c> in the vocabulary of docs/scope.md §7.3 — the most trustworthy of
    /// the three clocks, and the one audit and retention are measured against.
    /// </para>
    /// <para>
    /// Equipment clocks do not belong here. A PLC can be hours out and a gateway timestamp is only
    /// as good as its NTP; both are carried as ordinary fields inside the events that have them, so
    /// that they can disagree in the open instead of quietly overwriting each other.
    /// </para>
    /// <para>
    /// The type is <see cref="DateTimeOffset"/> and never <see cref="DateTime"/> (AGENTS.md K2):
    /// site DE1 observes daylight saving time, so a wall-clock reading without an offset is
    /// ambiguous for one hour every autumn. Analyzer NVM002 enforces this at build time.
    /// </para>
    /// </remarks>
    DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// The plant this fact belongs to, for example <c>NV1</c> or <c>DE1</c>.
    /// </summary>
    /// <remarks>
    /// Present on every event without exception (AGENTS.md K3). It is also the first segment of the
    /// routing key, <c>nvm.{site}.{context}.{event}.v{n}</c>, which is what lets one service
    /// subscribe to a single plant. Authorization filters on it server-side; a client asking nicely
    /// for its own site is not a control, and a cross-site leak is a security defect rather than a
    /// display bug (docs/scope.md §5.6).
    /// </remarks>
    string SiteId { get; }
}
