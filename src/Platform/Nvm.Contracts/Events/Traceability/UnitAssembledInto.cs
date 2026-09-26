namespace Nvm.Contracts.Events.Traceability;

/// <summary>Cell vào module, module hoặc cell vào pack: cạnh ASSOCIATION, tháo ra được (scope §6.4).</summary>
[EventContract("traceability", "unit-assembled-into")]
[EventVersion(1)]
public sealed record UnitAssembledInto(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string ChildSerialNumber, string ParentSerialNumber, string Position,
    string OperationRunId, string ActorId) : IDomainEvent;
