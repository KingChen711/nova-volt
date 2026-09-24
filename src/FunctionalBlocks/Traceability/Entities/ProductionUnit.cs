using System.Collections.Immutable;
using System.Text.Json;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Ports;

namespace Nvm.Traceability.Entities;

public enum ExecutionState { Scheduled, Running, Completed, Aborted }
public enum QualityState { Pending, Released, Held, Rework, Scrapped }
public enum LocationState { AtStation, InTransit, AtRack, Shipped }

public sealed record TransitionContext(ExecutionState Current, string StepCode,
    UnitRouting Routing, QualityState Quality, LocationState Location,
    ImmutableHashSet<string> ActorRoles, DateTimeOffset OccurredAt);

/// <summary>One serialized cell, module, or pack per stream. Quality/location remain independent facts.</summary>
public sealed class ProductionUnit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private ImmutableHashSet<string> _completedSteps = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    private ProductionUnit(string siteId, string serialNumber)
    {
        SiteId = siteId;
        SerialNumber = serialNumber;
    }

    public string SiteId { get; }
    public string SerialNumber { get; }
    public ProductionUnitKind Kind { get; private set; }
    public string ProductCode { get; private set; } = "";
    public string WorkOrderId { get; private set; } = "";
    public string RoutingVersion { get; private set; } = "";
    public string? CurrentStep { get; private set; }
    public string? OperationRunId { get; private set; }
    public string? EquipmentPath { get; private set; }
    public ExecutionState Execution { get; private set; } = ExecutionState.Scheduled;
    public QualityState Quality { get; private set; } = QualityState.Pending;
    public LocationState Location { get; private set; } = LocationState.AtStation;
    public ImmutableHashSet<string> CompletedSteps => _completedSteps;
    public long Version { get; private set; }

    public static ProductionUnit? Replay(EventStream? stream)
    {
        if (stream is null)
        {
            return null;
        }

        if (stream.Events.IsDefaultOrEmpty)
        {
            throw new InvalidDataException("A production unit stream must start with serialization.");
        }

        ProductionUnit? unit = null;
        long expectedVersion = 0;
        foreach (var stored in stream.Events)
        {
            if (stored.Version != ++expectedVersion ||
                !string.Equals(stored.SiteId, stream.SiteId, StringComparison.Ordinal) ||
                !string.Equals(stored.StreamId, stream.StreamId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Production unit stream order or identity is invalid.");
            }

            switch (stored.EventType)
            {
                case "com.novavolt.traceability.unit-serialized.v1":
                    if (unit is not null)
                    {
                        throw new InvalidDataException("Unit serialized twice in one stream.");
                    }

                    var born = Read<ProductionUnitSerialized>(stored);
                    if (!string.Equals(born.SiteId, stream.SiteId, StringComparison.Ordinal) ||
                        !string.Equals(born.SerialNumber, stream.StreamId, StringComparison.Ordinal) ||
                        !Nvm.Kernel.Identity.SerialNumber.TryParse(born.SerialNumber, out var serial))
                    {
                        throw new InvalidDataException("Serialized unit identity is invalid.");
                    }

                    unit = new ProductionUnit(born.SiteId, born.SerialNumber)
                    {
                        Kind = serial.Kind,
                        ProductCode = born.ProductCode,
                        WorkOrderId = born.WorkOrderId,
                        RoutingVersion = born.RoutingVersion
                    };
                    break;
                case "com.novavolt.traceability.process-step-started.v1":
                    EnsureBorn(unit, stored);
                    var started = Read<ProcessStepStarted>(stored);
                    EnsureEventIdentity(unit!, started.SiteId, started.SerialNumber);
                    unit!.CurrentStep = started.StepCode;
                    unit.OperationRunId = started.OperationRunId;
                    unit.EquipmentPath = started.EquipmentPath;
                    unit.Execution = ExecutionState.Running;
                    break;
                case "com.novavolt.traceability.process-step-completed.v1":
                    EnsureBorn(unit, stored);
                    var completed = Read<ProcessStepCompleted>(stored);
                    EnsureEventIdentity(unit!, completed.SiteId, completed.SerialNumber);
                    if (unit!.Execution != ExecutionState.Running ||
                        unit.CurrentStep != completed.StepCode ||
                        unit.OperationRunId != completed.OperationRunId)
                    {
                        throw new InvalidDataException("Completed step does not match the running step.");
                    }

                    unit._completedSteps = unit._completedSteps.Add(completed.StepCode);
                    unit.Execution = ExecutionState.Completed;
                    break;
                case "com.novavolt.traceability.unit-measurement-recorded.v1":
                    EnsureBorn(unit, stored);
                    var measurement = Read<UnitMeasurementRecorded>(stored);
                    EnsureEventIdentity(unit!, measurement.SiteId, measurement.SerialNumber);
                    break;
                default:
                    throw new InvalidDataException($"Unknown production unit event: {stored.EventType}.");
            }

            unit!.Version = stored.Version;
        }

        if (unit!.Version != stream.Version)
        {
            throw new InvalidDataException("Production unit stream head differs from replayed version.");
        }

        return unit;
    }

    public void RefreshIndependentStates(UnitGuardSnapshot guard)
    {
        Quality = guard.Quality;
        Location = guard.Location;
    }

    /// <summary>State after the fact about to be appended, used at the 100-event boundary.</summary>
    public string SnapshotAfter(long version, IDomainEvent fact)
    {
        var step = CurrentStep;
        var run = OperationRunId;
        var equipment = EquipmentPath;
        var execution = Execution;
        var completed = _completedSteps;
        switch (fact)
        {
            case ProcessStepStarted started:
                step = started.StepCode;
                run = started.OperationRunId;
                equipment = started.EquipmentPath;
                execution = ExecutionState.Running;
                break;
            case ProcessStepCompleted done:
                completed = completed.Add(done.StepCode);
                execution = ExecutionState.Completed;
                break;
            case UnitMeasurementRecorded:
                break;
            default:
                throw new ArgumentException("Fact does not update an existing production unit.", nameof(fact));
        }
        return JsonSerializer.Serialize(new
        {
            SiteId,
            SerialNumber,
            Kind = Kind.ToString(),
            ProductCode,
            WorkOrderId,
            RoutingVersion,
            CurrentStep = step,
            OperationRunId = run,
            EquipmentPath = equipment,
            Execution = execution.ToString(),
            Quality = Quality.ToString(),
            Location = Location.ToString(),
            CompletedSteps = completed.Order(StringComparer.Ordinal).ToArray(),
            Version = version
        }, Json);
    }

    public string? CanStart(TransitionContext context)
    {
        var blocked = Guard(context);
        if (blocked is not null)
        {
            return blocked;
        }

        if (Execution == ExecutionState.Running)
        {
            return UnitReasonCodes.StepAlreadyRunning;
        }

        if (_completedSteps.Contains(context.StepCode))
        {
            return UnitReasonCodes.RoutingViolation;
        }

        var index = -1;
        for (var i = 0; i < context.Routing.Steps.Length; i++)
        {
            if (string.Equals(context.Routing.Steps[i].Code, context.StepCode, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            return UnitReasonCodes.RoutingViolation;
        }

        if (index > 0 && !_completedSteps.Contains(context.Routing.Steps[index - 1].Code))
        {
            return UnitReasonCodes.RoutingViolation;
        }

        var role = context.Routing.Steps[index].RequiredRole;
        if (role is not null && !context.ActorRoles.Contains(role))
        {
            return UnitReasonCodes.UnauthorizedTransition;
        }

        return Allowed("StartStep", context.Current, ExecutionState.Running, context);
    }

    public string? CanComplete(TransitionContext context, string operationRunId)
    {
        var blocked = Guard(context);
        if (blocked is not null)
        {
            return blocked;
        }

        if (Execution != ExecutionState.Running || CurrentStep != context.StepCode)
        {
            return UnitReasonCodes.StepNotRunning;
        }

        if (OperationRunId != operationRunId)
        {
            return UnitReasonCodes.OperationRunMismatch;
        }

        return Allowed("CompleteStep", context.Current, ExecutionState.Completed, context);
    }

    private static string? Guard(TransitionContext context) => context.Quality switch
    {
        QualityState.Scrapped => UnitReasonCodes.Scrapped,
        QualityState.Held => UnitReasonCodes.QualityHold,
        _ => null
    };

    private static string? Allowed(string action, ExecutionState from, ExecutionState to,
        TransitionContext context)
    {
        foreach (var rule in context.Routing.Transitions)
        {
            if (rule.Action != action || rule.From != from || rule.To != to)
            {
                continue;
            }

            return rule.RequiredRole is null || context.ActorRoles.Contains(rule.RequiredRole)
                ? null : UnitReasonCodes.UnauthorizedTransition;
        }
        return UnitReasonCodes.RoutingViolation;
    }

    private static T Read<T>(StoredStreamEvent stored) where T : class
    {
        if (stored.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unexpected schema version {stored.SchemaVersion} for {stored.EventType}.");
        }

        var value = JsonSerializer.Deserialize<T>(stored.PayloadJson, Json)
            ?? throw new InvalidDataException("Production unit event payload is null.");
        if (value is Nvm.Contracts.Events.IDomainEvent domain && domain.EventId != stored.SourceEventId)
        {
            throw new InvalidDataException("Event ID differs from stream source event ID.");
        }

        return value;
    }

    private static void EnsureBorn(ProductionUnit? unit, StoredStreamEvent stored)
    {
        if (unit is null)
        {
            throw new InvalidDataException($"{stored.EventType} precedes serialization.");
        }
    }

    private static void EnsureEventIdentity(ProductionUnit unit, string siteId, string serialNumber)
    {
        if (unit.SiteId != siteId || unit.SerialNumber != serialNumber)
        {
            throw new InvalidDataException("Event belongs to another production unit.");
        }
    }
}
