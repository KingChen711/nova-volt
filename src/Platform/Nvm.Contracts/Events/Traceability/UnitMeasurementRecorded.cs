namespace Nvm.Contracts.Events.Traceability;

/// <summary>An accepted process measurement; it does not imply a quality decision or step completion.</summary>
[EventContract("traceability", "unit-measurement-recorded")]
[EventVersion(1)]
public sealed record UnitMeasurementRecorded(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string StepCode, string OperationRunId,
    string EquipmentPath, string SignalCode, decimal Value, string UnitOfMeasure,
    string ActorId) : IDomainEvent;
