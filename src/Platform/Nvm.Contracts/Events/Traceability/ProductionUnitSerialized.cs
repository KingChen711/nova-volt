namespace Nvm.Contracts.Events.Traceability;

[EventContract("traceability", "unit-serialized")]
[EventVersion(1)]
public sealed record ProductionUnitSerialized(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string UnitKind, string ProductCode,
    string WorkOrderId, string RoutingVersion, string ActorId) : IDomainEvent;
