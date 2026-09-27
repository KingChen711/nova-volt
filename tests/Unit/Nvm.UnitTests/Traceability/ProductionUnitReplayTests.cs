using System.Collections.Immutable;
using System.Text.Json;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Traceability;
using Nvm.Contracts.Queries;
using Nvm.Kernel.EventSourcing;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Entities;
using Nvm.Traceability.Ports;

namespace Nvm.UnitTests.Traceability;

/// <summary>Replay từ chối mọi stream không nhất quán, và guard chuyển trạng thái đi đúng routing + role.</summary>
public sealed class ProductionUnitReplayTests
{
    private const string Site = "NV1";
    private const string Serial = "NV1CL16238A00123";
    private static readonly DateTimeOffset At = new(2026, 8, 25, 3, 15, 42, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static ProductionUnitSerialized Born(string site = Site, string serial = Serial) =>
        new(Guid.NewGuid(), At, At, site, serial, "Cell", "NV-CELL-60AH", "WO-1", "r1", "op");

    private static ProcessStepStarted Started(string step = "STACK", string run = "run-1", string serial = Serial) =>
        new(Guid.NewGuid(), At, At, Site, serial, step, run, "NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01", "op");

    private static ProcessStepCompleted Completed(string step = "STACK", string run = "run-1") =>
        new(Guid.NewGuid(), At, At, Site, Serial, step, run, "op");

    private static UnitMeasurementRecorded Measured(string serial = Serial) =>
        new(Guid.NewGuid(), At, At, Site, serial, "STACK", "run-1", "NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01", "Voltage", 3.7m, "V", "op");

    private static readonly Dictionary<Type, string> Types = new()
    {
        [typeof(ProductionUnitSerialized)] = "com.novavolt.traceability.unit-serialized.v1",
        [typeof(ProcessStepStarted)] = "com.novavolt.traceability.process-step-started.v1",
        [typeof(ProcessStepCompleted)] = "com.novavolt.traceability.process-step-completed.v1",
        [typeof(UnitMeasurementRecorded)] = "com.novavolt.traceability.unit-measurement-recorded.v1",
    };

    private static StoredStreamEvent Stored(IDomainEvent fact, long version) =>
        new(version, Site, Serial, version, fact.EventId, Types[fact.GetType()], 1,
            JsonSerializer.Serialize(fact, fact.GetType(), Json), "{}", At, At);

    private static EventStream Stream(params IDomainEvent[] facts) => Stream(facts.Select((f, i) => Stored(f, i + 1)).ToArray());

    private static EventStream Stream(StoredStreamEvent[] events, long? head = null) =>
        new(Site, Serial, "production-unit", head ?? events.Length, [.. events]);

    [Fact]
    public void Replay_OfNull_IsNull_AndOfAnEmptyStream_Throws()
    {
        ProductionUnit.Replay(null).ShouldBeNull();
        Should.Throw<InvalidDataException>(() => ProductionUnit.Replay(Stream([])));
        Should.Throw<InvalidDataException>(() => ProductionUnit.Replay(new EventStream(Site, Serial, "production-unit", 0, default)));
    }

    [Fact]
    public void Replay_RebuildsIdentityAndStepState()
    {
        var unit = ProductionUnit.Replay(Stream(Born(), Started(), Measured(), Completed(), Started("TABWELD", "run-2")))!;
        (unit.SiteId, unit.SerialNumber, unit.ProductCode, unit.WorkOrderId, unit.RoutingVersion)
            .ShouldBe((Site, Serial, "NV-CELL-60AH", "WO-1", "r1"));
        unit.Kind.ToString().ShouldBe("Cell");
        (unit.CurrentStep, unit.OperationRunId, unit.Execution, unit.Version).ShouldBe(("TABWELD", "run-2", ExecutionState.Running, 5L));
        unit.EquipmentPath.ShouldBe("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01");
        unit.CompletedSteps.ShouldBe(["STACK"]);
        unit.Location.ShouldBe(LocationState.AtStation);

        var done = ProductionUnit.Replay(Stream(Born(), Started(), Completed()))!;
        done.Execution.ShouldBe(ExecutionState.Completed);
        ProductionUnit.Replay(Stream(Born()))!.Execution.ShouldBe(ExecutionState.Scheduled);
    }

    public static TheoryData<string> BrokenStreams() =>
    [
        "gap", "wrong-site", "wrong-stream", "twice", "before-birth", "unknown", "schema", "event-id", "born-site",
        "born-serial", "bad-serial", "other-unit", "complete-not-running", "complete-other-step", "complete-other-run", "head",
        "measure-other-unit", "complete-before-birth", "measure-before-birth",
    ];

    [Theory]
    [MemberData(nameof(BrokenStreams))]
    public void Replay_RejectsInconsistentStreams(string kind)
    {
        var born = Stored(Born(), 1);
        var started = Stored(Started(), 2);
        var stream = kind switch
        {
            "gap" => Stream([born, started with { Version = 3 }], 3),
            "wrong-site" => Stream([born, started with { SiteId = "DE1" }]),
            "wrong-stream" => Stream([born, started with { StreamId = "NV1CL16238A00124" }]),
            "twice" => Stream(Born(), Born()),
            "before-birth" => Stream([Stored(Started(), 1)]),
            "complete-before-birth" => Stream([Stored(Completed(), 1)]),
            "measure-before-birth" => Stream([Stored(Measured(), 1)]),
            "unknown" => Stream([born, started with { EventType = "com.novavolt.traceability.unit-teleported.v1" }]),
            "schema" => Stream([born, started with { SchemaVersion = 2 }]),
            "event-id" => Stream([born, started with { SourceEventId = Guid.NewGuid() }]),
            "born-site" => Stream(Born(site: "DE1")),
            "born-serial" => Stream(Born(serial: "NV1CL16238A00124")),
            "bad-serial" => Stream([Stored(Born(serial: "NOT-A-SERIAL"), 1) with { StreamId = "NOT-A-SERIAL" }]) with { StreamId = "NOT-A-SERIAL" },
            "other-unit" => Stream(Born(), Started(serial: "NV1CL16238A00124")),
            "measure-other-unit" => Stream(Born(), Measured(serial: "NV1CL16238A00124")),
            "complete-not-running" => Stream(Born(), Completed()),
            "complete-other-step" => Stream(Born(), Started(), Completed(step: "TABWELD")),
            "complete-other-run" => Stream(Born(), Started(), Completed(run: "run-9")),
            "head" => Stream([born, started], head: 3),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Should.Throw<InvalidDataException>(() => ProductionUnit.Replay(stream));
    }

    [Fact]
    public void Replay_RejectsANullPayload()
    {
        var stream = Stream([Stored(Born(), 1) with { PayloadJson = "null" }]);
        Should.Throw<InvalidDataException>(() => ProductionUnit.Replay(stream));
    }

    private static UnitRouting Routing(string? stepRole = null, string? transitionRole = null) =>
        new(Site, "NV-CELL-60AH", "r1", [new("STACK"), new("TABWELD", stepRole)],
        [
            new("StartStep", ExecutionState.Scheduled, ExecutionState.Running, transitionRole),
            new("StartStep", ExecutionState.Completed, ExecutionState.Running, transitionRole),
            new("CompleteStep", ExecutionState.Running, ExecutionState.Completed, transitionRole),
        ]);

    private static TransitionContext Context(ExecutionState current, string step, UnitRouting routing,
        UnitQualityFacet? quality = null, params string[] roles) =>
        new(current, step, routing, quality ?? UnitQualityFacet.Pending, LocationState.AtStation,
            [.. roles], At);

    [Fact]
    public void CanStart_FollowsRoutingOrder_AndRoles()
    {
        var fresh = ProductionUnit.Replay(Stream(Born()))!;
        fresh.CanStart(Context(ExecutionState.Scheduled, "STACK", Routing())).ShouldBeNull();
        fresh.CanStart(Context(ExecutionState.Scheduled, "TABWELD", Routing())).ShouldBe(UnitReasonCodes.RoutingViolation);
        fresh.CanStart(Context(ExecutionState.Scheduled, "PAINT", Routing())).ShouldBe(UnitReasonCodes.RoutingViolation);
        fresh.CanStart(Context(ExecutionState.Scheduled, "STACK", Routing(transitionRole: "LineLeader")))
            .ShouldBe(UnitReasonCodes.UnauthorizedTransition);
        fresh.CanStart(Context(ExecutionState.Scheduled, "STACK", Routing(transitionRole: "LineLeader"), null, "LineLeader"))
            .ShouldBeNull();
        // Không có luật chuyển tương ứng trạng thái hiện tại.
        fresh.CanStart(Context(ExecutionState.Aborted, "STACK", Routing())).ShouldBe(UnitReasonCodes.RoutingViolation);

        var running = ProductionUnit.Replay(Stream(Born(), Started()))!;
        running.CanStart(Context(ExecutionState.Running, "TABWELD", Routing())).ShouldBe(UnitReasonCodes.StepAlreadyRunning);

        var stacked = ProductionUnit.Replay(Stream(Born(), Started(), Completed()))!;
        stacked.CanStart(Context(ExecutionState.Completed, "STACK", Routing())).ShouldBe(UnitReasonCodes.RoutingViolation);
        stacked.CanStart(Context(ExecutionState.Completed, "TABWELD", Routing())).ShouldBeNull();
        stacked.CanStart(Context(ExecutionState.Completed, "TABWELD", Routing(stepRole: "Welder")))
            .ShouldBe(UnitReasonCodes.UnauthorizedTransition);
        stacked.CanStart(Context(ExecutionState.Completed, "TABWELD", Routing(stepRole: "Welder"), null, "Welder")).ShouldBeNull();
    }

    [Fact]
    public void QualityFacet_BlocksBothTransitions_WithItsOwnReason()
    {
        var held = new UnitQualityFacet("Held", "QUALITY_HOLD");
        ProductionUnit.Replay(Stream(Born()))!.CanStart(Context(ExecutionState.Scheduled, "STACK", Routing(), held))
            .ShouldBe("QUALITY_HOLD");
        ProductionUnit.Replay(Stream(Born(), Started()))!.CanComplete(Context(ExecutionState.Running, "STACK", Routing(), held), "run-1")
            .ShouldBe("QUALITY_HOLD");
    }

    [Fact]
    public void CanComplete_RequiresTheRunningStepAndRun()
    {
        var running = ProductionUnit.Replay(Stream(Born(), Started()))!;
        running.CanComplete(Context(ExecutionState.Running, "STACK", Routing()), "run-1").ShouldBeNull();
        running.CanComplete(Context(ExecutionState.Running, "TABWELD", Routing()), "run-1").ShouldBe(UnitReasonCodes.StepNotRunning);
        running.CanComplete(Context(ExecutionState.Running, "STACK", Routing()), "run-2").ShouldBe(UnitReasonCodes.OperationRunMismatch);
        running.CanComplete(Context(ExecutionState.Running, "STACK", Routing(transitionRole: "LineLeader")), "run-1")
            .ShouldBe(UnitReasonCodes.UnauthorizedTransition);
        ProductionUnit.Replay(Stream(Born(), Started(), Completed()))!
            .CanComplete(Context(ExecutionState.Completed, "STACK", Routing()), "run-1").ShouldBe(UnitReasonCodes.StepNotRunning);
    }

    [Fact]
    public void SnapshotAfter_DescribesThePostEventState_WithoutMutatingTheUnit()
    {
        var unit = ProductionUnit.Replay(Stream(Born(), Started()))!;
        using var afterComplete = JsonDocument.Parse(unit.SnapshotAfter(3, Completed()));
        afterComplete.RootElement.GetProperty("execution").GetString().ShouldBe("Completed");
        afterComplete.RootElement.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()).ShouldBe(["STACK"]);
        afterComplete.RootElement.GetProperty("version").GetInt64().ShouldBe(3);
        afterComplete.RootElement.GetProperty("location").GetString().ShouldBe("AtStation");
        unit.Execution.ShouldBe(ExecutionState.Running);
        unit.CompletedSteps.ShouldBeEmpty();

        using var afterStart = JsonDocument.Parse(unit.SnapshotAfter(3, Started("TABWELD", "run-2")));
        afterStart.RootElement.GetProperty("currentStep").GetString().ShouldBe("TABWELD");
        afterStart.RootElement.GetProperty("operationRunId").GetString().ShouldBe("run-2");
        afterStart.RootElement.GetProperty("execution").GetString().ShouldBe("Running");

        using var afterMeasure = JsonDocument.Parse(unit.SnapshotAfter(3, Measured()));
        afterMeasure.RootElement.GetProperty("currentStep").GetString().ShouldBe("STACK");
        afterMeasure.RootElement.GetProperty("serialNumber").GetString().ShouldBe(Serial);
        afterMeasure.RootElement.GetProperty("kind").GetString().ShouldBe("Cell");

        Should.Throw<ArgumentException>(() => unit.SnapshotAfter(3, Born()));
    }

    [Fact]
    public void RefreshIndependentStates_TakesLocationFromTheGuard()
    {
        var unit = ProductionUnit.Replay(Stream(Born()))!;
        unit.RefreshIndependentStates(new UnitGuardSnapshot(UnitQualityFacet.Pending, LocationState.AtRack,
            ImmutableHashSet<string>.Empty));
        unit.Location.ShouldBe(LocationState.AtRack);
    }
}
