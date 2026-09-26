namespace Nvm.Contracts.Events.Traceability;

/// <summary>Rework: tháo unit khỏi cha. Cạnh cũ được đánh dấu gỡ, không bị xoá.</summary>
[EventContract("traceability", "unit-removed-from")]
[EventVersion(1)]
public sealed record UnitRemovedFrom(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string ChildSerialNumber, string ParentSerialNumber, string ReasonCode,
    string OperationRunId, string ActorId) : IDomainEvent;
