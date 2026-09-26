using System.Collections.Immutable;
using System.Text.Json;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Traceability;
using Nvm.Contracts.Queries;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Entities;
using Nvm.Traceability.Ports;

namespace Nvm.Traceability.Handlers;

/// <summary>All writes join the durable idempotency claim transaction supplied by the host.</summary>
public sealed class TraceabilityCommandProcessor(
    IEventStore events, IRoutingDirectory routings, IUnitGuard guard,
    ISerialReservation serials, IDuplicateSerialQuarantine quarantine, IUnitQualityFacet quality,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<UnitCommandResult> SerializeAsync(SerializeUnitCommand command, CancellationToken ct)
    {
        var validation = Validate(command);
        if (validation is not null)
        {
            return Reject(validation);
        }

        if (!SerialNumber.TryParse(command.SerialNumber, out var serial))
        {
            return Reject(UnitReasonCodes.InvalidSerial);
        }

        if (serial.SiteCode != command.SiteId)
        {
            return Reject(UnitReasonCodes.SiteMismatch);
        }

        if (Missing(command.ProductCode, command.WorkOrderId, command.RoutingVersion) ||
            command.ProductCode.Length > 100 || command.RoutingVersion.Length > 50)
        {
            return Reject(UnitReasonCodes.InvalidInput);
        }

        if (await routings.FindAsync(command.SiteId, command.ProductCode, command.RoutingVersion, ct)
                .ConfigureAwait(false) is null)
        {
            return Reject(UnitReasonCodes.RoutingNotFound);
        }

        var reservation = await serials.ReserveAsync(command.SiteId, command.SerialNumber,
            command.IdempotencyKey.Value, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (reservation == SerialReservationOutcome.Duplicate)
        {
            var incident = new DuplicateSerialDetected(command.IdempotencyKey.Value, command.OccurredAt,
                now, command.SiteId, command.SerialNumber, command.SubmissionId, command.ActorId,
                UnitReasonCodes.DuplicateSerial);
            // The incident stream preserves each attempted physical serialization separately from the
            // existing unit. The audit fact and the quality facet's hold both join this transaction.
            await quarantine.RecordAsync(command.SiteId, command.SerialNumber, command.SubmissionId,
                incident.EventId, command.ActorId, command.OccurredAt, ct).ConfigureAwait(false);
            await quality.QuarantineForIncidentAsync(command.SiteId, command.SerialNumber, incident.EventId,
                UnitReasonCodes.DuplicateSerial, command.ActorId, command.OccurredAt, ct).ConfigureAwait(false);
            var incidentVersion = await AppendAsync(command.SiteId, "duplicate:" + command.SubmissionId,
                "duplicate-serial", 0, incident, now, ct).ConfigureAwait(false);
            return new UnitCommandResult(true, UnitReasonCodes.DuplicateSerial,
                incident.EventId, incidentVersion, Quarantined: true);
        }

        var created = new ProductionUnitSerialized(command.IdempotencyKey.Value, command.OccurredAt,
            now, command.SiteId, command.SerialNumber, serial.Kind.ToString(), command.ProductCode,
            command.WorkOrderId, command.RoutingVersion, command.ActorId);
        var version = await AppendAsync(command.SiteId, command.SerialNumber, "production-unit", 0,
            created, now, ct).ConfigureAwait(false);
        return Accepted(created.EventId, version);
    }

    public async Task<UnitCommandResult> StartAsync(StartStepCommand command, CancellationToken ct)
    {
        var context = await LoadAsync(command, ct).ConfigureAwait(false);
        if (context.Error is not null)
        {
            return Reject(context.Error);
        }

        if (Missing(command.StepCode, command.OperationRunId, command.EquipmentPath))
        {
            return Reject(UnitReasonCodes.InvalidInput);
        }

        var transition = MakeContext(context, command.StepCode, command.OccurredAt);
        var reason = context.Unit!.CanStart(transition);
        if (reason is not null)
        {
            return Reject(reason);
        }

        var now = clock.GetUtcNow();
        var started = new ProcessStepStarted(command.IdempotencyKey.Value, command.OccurredAt, now,
            command.SiteId, command.SerialNumber, command.StepCode, command.OperationRunId,
            command.EquipmentPath, command.ActorId);
        var version = await AppendAsync(command.SiteId, command.SerialNumber, "production-unit",
            context.Unit.Version, started, now, ct, context.Unit).ConfigureAwait(false);
        return Accepted(started.EventId, version);
    }

    public async Task<UnitCommandResult> CompleteAsync(CompleteStepCommand command, CancellationToken ct)
    {
        var context = await LoadAsync(command, ct).ConfigureAwait(false);
        if (context.Error is not null)
        {
            return Reject(context.Error);
        }

        if (Missing(command.StepCode, command.OperationRunId))
        {
            return Reject(UnitReasonCodes.InvalidInput);
        }

        var transition = MakeContext(context, command.StepCode, command.OccurredAt);
        var reason = context.Unit!.CanComplete(transition, command.OperationRunId);
        if (reason is not null)
        {
            return Reject(reason);
        }

        var now = clock.GetUtcNow();
        var completed = new ProcessStepCompleted(command.IdempotencyKey.Value, command.OccurredAt,
            now, command.SiteId, command.SerialNumber, command.StepCode, command.OperationRunId,
            command.ActorId);
        var version = await AppendAsync(command.SiteId, command.SerialNumber, "production-unit",
            context.Unit.Version, completed, now, ct, context.Unit).ConfigureAwait(false);
        return Accepted(completed.EventId, version);
    }

    public async Task<UnitCommandResult> RecordMeasurementAsync(RecordMeasurementCommand command,
        CancellationToken ct)
    {
        var context = await LoadAsync(command, ct).ConfigureAwait(false);
        if (context.Error is not null)
        {
            return Reject(context.Error);
        }

        if (Missing(command.StepCode, command.OperationRunId, command.EquipmentPath,
                command.SignalCode, command.UnitOfMeasure))
        {
            return Reject(UnitReasonCodes.InvalidInput);
        }

        if (context.Guard!.Quality.BlockingReasonCode is { } blocked)
        {
            return Reject(blocked);
        }

        if (context.Unit!.Execution != ExecutionState.Running ||
            context.Unit.CurrentStep != command.StepCode)
        {
            return Reject(UnitReasonCodes.StepNotRunning);
        }

        if (context.Unit.OperationRunId != command.OperationRunId)
        {
            return Reject(UnitReasonCodes.OperationRunMismatch);
        }

        if (context.Unit.EquipmentPath != command.EquipmentPath)
        {
            return Reject(UnitReasonCodes.EquipmentMismatch);
        }

        var now = clock.GetUtcNow();
        var recorded = new UnitMeasurementRecorded(command.IdempotencyKey.Value, command.OccurredAt,
            now, command.SiteId, command.SerialNumber, command.StepCode, command.OperationRunId,
            command.EquipmentPath, command.SignalCode, command.Value, command.UnitOfMeasure,
            command.ActorId);
        var version = await AppendAsync(command.SiteId, command.SerialNumber, "production-unit",
            context.Unit.Version, recorded, now, ct, context.Unit).ConfigureAwait(false);
        return Accepted(recorded.EventId, version);
    }

    private async Task<(ProductionUnit? Unit, UnitRouting? Routing, UnitGuardSnapshot? Guard, string? Error)>
        LoadAsync(UnitCommand command, CancellationToken ct)
    {
        var validation = Validate(command);
        if (validation is not null)
        {
            return (null, null, null, validation);
        }

        if (!SerialNumber.TryParse(command.SerialNumber, out var serial))
        {
            return (null, null, null, UnitReasonCodes.InvalidSerial);
        }

        if (serial.SiteCode != command.SiteId)
        {
            return (null, null, null, UnitReasonCodes.SiteMismatch);
        }

        var stream = await events.ReadStreamAsync(command.SiteId, command.SerialNumber, ct)
            .ConfigureAwait(false);
        var unit = ProductionUnit.Replay(stream);
        if (unit is null)
        {
            return (null, null, null, UnitReasonCodes.UnitNotFound);
        }

        var routing = await routings.FindAsync(command.SiteId, unit.ProductCode, unit.RoutingVersion, ct)
            .ConfigureAwait(false);
        if (routing is null)
        {
            return (null, null, null, UnitReasonCodes.RoutingNotFound);
        }

        var snapshot = await guard.ReadAsync(command.SiteId, command.SerialNumber, command.ActorId, ct)
            .ConfigureAwait(false);
        unit.RefreshIndependentStates(snapshot);
        return (unit, routing, snapshot, null);
    }

    private static TransitionContext MakeContext(
        (ProductionUnit? Unit, UnitRouting? Routing, UnitGuardSnapshot? Guard, string? Error) loaded,
        string stepCode, DateTimeOffset occurredAt) =>
        new(loaded.Unit!.Execution, stepCode, loaded.Routing!, loaded.Guard!.Quality,
            loaded.Guard.Location, loaded.Guard.ActorRoles, occurredAt);

    private async Task<long> AppendAsync(string siteId, string streamId, string streamType,
        long expectedVersion, IDomainEvent fact, DateTimeOffset recordedAt, CancellationToken ct,
        ProductionUnit? unit = null)
    {
        var type = EventTypeName.Of(fact.GetType());
        var payload = JsonSerializer.Serialize(fact, fact.GetType(), Json);
        var serialNumber = fact switch
        {
            ProductionUnitSerialized value => value.SerialNumber,
            ProcessStepStarted value => value.SerialNumber,
            ProcessStepCompleted value => value.SerialNumber,
            UnitMeasurementRecorded value => value.SerialNumber,
            DuplicateSerialDetected value => value.SerialNumber,
            _ => throw new InvalidOperationException("Unknown unit event metadata contract.")
        };
        var serial = SerialNumber.Parse(serialNumber);
        var metadata = JsonSerializer.Serialize(new
        {
            subject = $"urn:trace-unit:{serial.Kind.ToString().ToLowerInvariant()}:{serialNumber}",
            correlationid = fact.EventId.ToString(),
            causationid = fact.EventId.ToString(),
            partitionkey = $"{siteId}:{serialNumber}"
        }, Json);
        var entry = new NewStreamEvent(fact.EventId, type.Value, type.Version, payload, metadata,
            fact.OccurredAt, recordedAt);
        var version = await events.AppendAsync(siteId, streamId, streamType, expectedVersion,
            ImmutableArray.Create(entry), ct).ConfigureAwait(false);
        if (version % 100 == 0 && unit is not null)
        {
            await events.SaveSnapshotAsync(new EventSnapshot(siteId, streamId, version,
                unit.SnapshotAfter(version, fact), recordedAt), ct).ConfigureAwait(false);
        }
        return version;
    }

    private static bool Missing(params string[] parts) => parts.Any(string.IsNullOrWhiteSpace);

    private static string? Validate(UnitCommand command) =>
        Missing(command.SiteId, command.ActorId, command.SubmissionId) ||
        command.ActorId.Length > 200 || command.SubmissionId.Length > 190
            ? UnitReasonCodes.InvalidInput : null;

    private static UnitCommandResult Accepted(Guid id, long version) =>
        new(true, UnitReasonCodes.Accepted, id, version);

    private static UnitCommandResult Reject(string reason) => new(false, reason, null, null);
}
