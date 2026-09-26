using System.Collections.Immutable;
using Nvm.Contracts.Queries;
using Nvm.Traceability.Entities;

namespace Nvm.Traceability.Ports;

public sealed record RoutingStep(string Code, string? RequiredRole = null);

public sealed record TransitionRule(string Action, ExecutionState From, ExecutionState To,
    string? RequiredRole = null);

public sealed record UnitRouting(string SiteId, string ProductCode, string Version,
    ImmutableArray<RoutingStep> Steps, ImmutableArray<TransitionRule> Transitions);

/// <summary>Versioned route from plant master data. The FB does not infer routing from equipment names.</summary>
public interface IRoutingDirectory
{
    Task<UnitRouting?> FindAsync(string siteId, string productCode, string routingVersion,
        CancellationToken cancellationToken);
}

public sealed record UnitGuardSnapshot(UnitQualityFacet Quality, LocationState Location,
    ImmutableHashSet<string> ActorRoles);

/// <summary>Reads the quality facet, logistics, and authorization facts through a host adapter.</summary>
public interface IUnitGuard
{
    Task<UnitGuardSnapshot> ReadAsync(string siteId, string serialNumber, string actorId,
        CancellationToken cancellationToken);
}

public enum SerialReservationOutcome { Reserved, Duplicate }

/// <summary>Database-backed, site-scoped unique reservation joined to the command transaction.</summary>
public interface ISerialReservation
{
    Task<SerialReservationOutcome> ReserveAsync(string siteId, string serialNumber,
        Guid eventId, CancellationToken cancellationToken);
}

/// <summary>Persist the duplicate incident within the command transaction; the hold belongs to the quality facet.</summary>
public interface IDuplicateSerialQuarantine
{
    Task RecordAsync(string siteId, string serialNumber, string submissionId, Guid eventId,
        string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}
