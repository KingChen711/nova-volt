namespace Nvm.Contracts.Events.Traceability;

[EventContract("traceability", "duplicate-serial-detected")]
[EventVersion(1)]
public sealed record DuplicateSerialDetected(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string SubmissionId,
    string ActorId, string ReasonCode) : IDomainEvent;
