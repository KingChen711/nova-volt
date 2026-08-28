namespace Nvm.Contracts.Events.Quality;

/// <summary>A measurement taken on a production unit was accepted into the system.</summary>
/// <param name="EventId">
/// Identity of this occurrence, and the whole point of this event. See <see cref="IDomainEvent.EventId"/>.
/// </param>
/// <param name="OccurredAt">When ingestion committed the reading. See <see cref="IDomainEvent.OccurredAt"/>.</param>
/// <param name="SiteId">The plant the reading belongs to (K3).</param>
/// <param name="EquipmentPath">Where it was taken, as the resolved ISA-95 path.</param>
/// <param name="UnitId">
/// The unit the evaluated result belongs to. Nullable so the immutable v1 golden document remains
/// readable; current ingestion publication requires a non-empty value.
/// </param>
/// <param name="StepCode">The process step the equipment performs.</param>
/// <param name="SignalCode">The metric, in the plant's own vocabulary.</param>
/// <param name="DeviceTimestamp">When the device says it took the reading.</param>
/// <param name="GatewayTimestamp">When the edge gateway received the publish carrying it.</param>
/// <param name="ClockQuality"><c>Good</c>, <c>Drifted</c> or <c>Unknown</c> (scope.md §7.3).</param>
/// <param name="ValueKind">Which of the value fields carries the reading.</param>
/// <param name="RealValue">The reading, for a continuous signal.</param>
/// <param name="IntegerValue">The reading, for a counted signal.</param>
/// <param name="BooleanValue">The reading, for a flag.</param>
/// <param name="TextValue">The reading, for a serial or a label.</param>
/// <remarks>
/// <para>
/// <b><see cref="EventId"/> is the <c>source_event_id</c> of scope.md §7.2</b> — the deterministic
/// UUIDv5 built from the natural key, the same value the row carries in
/// <c>ingest.processed_message</c>. It becomes <c>ce_id</c> on the wire, which is what joins device
/// deduplication to command deduplication. R-M1-6 says a drift between those two values would only
/// become visible at M2; this is the join it becomes visible at, and
/// <c>MeasurementRecordedPublishingTests</c> is what reads both back and compares them.
/// </para>
/// <para>
/// <b>Not every reading becomes one of these.</b> scope.md §5.5 draws the line: a continuous
/// observation is telemetry and stops at TimescaleDB, while an <i>evaluated</i> value — the OCV that
/// grades a cell, the capacity a formation cycle finished at — is a business fact and belongs on the
/// bus. Current ingestion requires both a configured signal code and a non-empty
/// <see cref="UnitId"/>. Publishing one of these per formation sample would put five thousand events
/// a second on a bus that exists to carry decisions, and would turn the event store into the
/// time-series database it sits next to.
/// </para>
/// <para>
/// All three timestamps travel, none of them as <see cref="OccurredAt"/>. A consumer computing a
/// production shift needs <see cref="DeviceTimestamp"/>; one auditing needs
/// <see cref="OccurredAt"/>; one deciding whether to believe either needs
/// <see cref="ClockQuality"/>. Collapsing them here would make that decision unavailable downstream,
/// after ingestion went to the trouble of keeping them apart.
/// </para>
/// </remarks>
[EventContract("quality", "measurement-recorded")]
[EventVersion(1)]
public sealed record MeasurementRecorded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string SiteId,
    string EquipmentPath,
    string? UnitId,
    string StepCode,
    string SignalCode,
    DateTimeOffset DeviceTimestamp,
    DateTimeOffset GatewayTimestamp,
    string ClockQuality,
    string ValueKind,
    double? RealValue,
    long? IntegerValue,
    bool? BooleanValue,
    string? TextValue) : IDomainEvent;
