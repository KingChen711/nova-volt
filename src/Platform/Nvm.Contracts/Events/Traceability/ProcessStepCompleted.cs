namespace Nvm.Contracts.Events.Traceability;

[EventContract("traceability", "process-step-completed")]
[EventVersion(1)]
public sealed record ProcessStepCompleted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string StepCode, string OperationRunId,
    string ActorId) : IDomainEvent;
