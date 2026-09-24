namespace Nvm.Contracts.Events.Traceability;

[EventContract("traceability", "process-step-started")]
[EventVersion(1)]
public sealed record ProcessStepStarted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string StepCode, string OperationRunId,
    string EquipmentPath, string ActorId) : IDomainEvent;
