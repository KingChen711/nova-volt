using System.Collections.Immutable;
using Nvm.Kernel.EventSourcing;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Entities;
using Nvm.Traceability.Handlers;
using Nvm.Traceability.Ports;

namespace Nvm.UnitTests.Traceability;

public sealed class ProductionUnitWorkflowTests
{
    private const string Site = "NV1";
    private const string Serial = "NV1CL16238A00123";
    private static readonly DateTimeOffset At = new(2026, 8, 25, 3, 15, 42, TimeSpan.Zero);

    [Fact]
    public async Task Workflow_ReplaysEachStep_WithoutChangingIndependentStates()
    {
        var fixture = new Fixture();
        (await fixture.Processor.SerializeAsync(Serialize("birth"), CancellationToken.None)).Accepted.ShouldBeTrue();
        (await fixture.Processor.StartAsync(Start("start-stack", "STACK", "run-1"), CancellationToken.None)).Accepted.ShouldBeTrue();
        (await fixture.Processor.RecordMeasurementAsync(
            Measure("measure", "STACK", "run-1"), CancellationToken.None)).Accepted.ShouldBeTrue();
        (await fixture.Processor.CompleteAsync(Complete("complete-stack", "STACK", "run-1"), CancellationToken.None)).Accepted.ShouldBeTrue();
        (await fixture.Processor.StartAsync(Start("start-weld", "TABWELD", "run-2"), CancellationToken.None)).Accepted.ShouldBeTrue();

        var unit = ProductionUnit.Replay(await fixture.Events.ReadStreamAsync(Site, Serial, CancellationToken.None));
        unit.ShouldNotBeNull();
        unit.Version.ShouldBe(5);
        unit.Execution.ShouldBe(ExecutionState.Running);
        unit.CurrentStep.ShouldBe("TABWELD");
        unit.CompletedSteps.ShouldContain("STACK");
        unit.Quality.ShouldBe(QualityState.Pending);
        unit.Location.ShouldBe(LocationState.AtStation);
    }

    [Fact]
    public async Task StartStep_WhenPreviousStepNotDone_IsRejectedWithRoutingViolation()
    {
        var fixture = new Fixture();
        await fixture.Processor.SerializeAsync(Serialize("birth"), CancellationToken.None);

        var result = await fixture.Processor.StartAsync(
            Start("skip", "TABWELD", "run-2"), CancellationToken.None);

        result.Accepted.ShouldBeFalse();
        result.ReasonCode.ShouldBe(UnitReasonCodes.RoutingViolation);
        (await fixture.Events.ReadStreamAsync(Site, Serial, CancellationToken.None))!.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData(QualityState.Held, UnitReasonCodes.QualityHold)]
    [InlineData(QualityState.Scrapped, UnitReasonCodes.Scrapped)]
    public async Task StartStep_WhenQualityBlocks_IsRejected(QualityState quality, string reason)
    {
        var fixture = new Fixture { Quality = quality };
        await fixture.Processor.SerializeAsync(Serialize("birth"), CancellationToken.None);

        var result = await fixture.Processor.StartAsync(
            Start("start", "STACK", "run-1"), CancellationToken.None);

        result.ReasonCode.ShouldBe(reason);
        result.Accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task SerializeUnit_WhenSerialAlreadyReserved_WritesSeparateIncidentAndQuarantines()
    {
        var fixture = new Fixture();
        var first = await fixture.Processor.SerializeAsync(Serialize("physical-1"), CancellationToken.None);
        var second = await fixture.Processor.SerializeAsync(Serialize("physical-2"), CancellationToken.None);

        first.Accepted.ShouldBeTrue();
        second.Accepted.ShouldBeTrue();
        second.Quarantined.ShouldBeTrue();
        second.ReasonCode.ShouldBe(UnitReasonCodes.DuplicateSerial);
        fixture.Incidents.ShouldBe(1);
        (await fixture.Events.ReadStreamAsync(Site, Serial, CancellationToken.None))!.Version.ShouldBe(1);
        (await fixture.Events.ReadStreamAsync(Site, "duplicate:physical-2", CancellationToken.None))!.Version.ShouldBe(1);
    }

    [Fact]
    public async Task CompleteStep_WhenOperationRunDiffers_IsRejected()
    {
        var fixture = new Fixture();
        await fixture.Processor.SerializeAsync(Serialize("birth"), CancellationToken.None);
        await fixture.Processor.StartAsync(Start("start", "STACK", "run-1"), CancellationToken.None);

        var result = await fixture.Processor.CompleteAsync(
            Complete("finish", "STACK", "other-run"), CancellationToken.None);

        result.Accepted.ShouldBeFalse();
        result.ReasonCode.ShouldBe(UnitReasonCodes.OperationRunMismatch);
    }

    [Fact]
    public async Task HundredthUnitFact_WritesSnapshotOfPostEventState()
    {
        var fixture = new Fixture();
        await fixture.Processor.SerializeAsync(Serialize("birth"), CancellationToken.None);
        await fixture.Processor.StartAsync(Start("start", "STACK", "run-1"), CancellationToken.None);
        for (var i = 0; i < 98; i++)
        {
            var result = await fixture.Processor.RecordMeasurementAsync(
                Measure("measurement-" + i, "STACK", "run-1"), CancellationToken.None);
            result.Accepted.ShouldBeTrue();
        }
        var snapshot = await fixture.Events.ReadSnapshotAsync(Site, Serial, CancellationToken.None);
        snapshot.ShouldNotBeNull();
        snapshot.Version.ShouldBe(100);
        using var state = System.Text.Json.JsonDocument.Parse(snapshot.StateJson);
        state.RootElement.GetProperty("version").GetInt64().ShouldBe(100);
        state.RootElement.GetProperty("execution").GetString().ShouldBe("Running");
        state.RootElement.GetProperty("currentStep").GetString().ShouldBe("STACK");
    }

    [Fact]
    public void MalformedSerializeCommand_IsRejectedByValidator()
    {
        var invalid = new SerializeUnitCommand(Site, "operator", "same-submission", Serial, At,
            "", "WO-1", "r1");
        new SerializeUnitValidator().Validate(invalid).Select(failure => failure.Field)
            .ShouldContain(nameof(invalid.ProductCode));
    }

    private static SerializeUnitCommand Serialize(string submission) =>
        new(Site, "operator", submission, Serial, At, "PRODUCT", "WO-1", "r1");

    private static StartStepCommand Start(string submission, string step, string run) =>
        new(Site, "operator", submission, Serial, At, step, run, "station-1");

    private static CompleteStepCommand Complete(string submission, string step, string run) =>
        new(Site, "operator", submission, Serial, At, step, run);

    private static RecordMeasurementCommand Measure(string submission, string step, string run) =>
        new(Site, "operator", submission, Serial, At, step, run, "station-1", "Voltage", 3.72m, "V");

    private sealed class Fixture : IRoutingDirectory, IUnitGuard, ISerialReservation, IDuplicateSerialQuarantine
    {
        private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
        public MemoryEvents Events { get; } = new();
        public int Incidents { get; private set; }
        public QualityState Quality { get; set; } = QualityState.Pending;
        public TraceabilityCommandProcessor Processor => new(Events, this, this, this, this, TimeProvider.System);

        public Task<UnitRouting?> FindAsync(string siteId, string productCode, string routingVersion,
            CancellationToken cancellationToken)
        {
            UnitRouting route = new(siteId, productCode, routingVersion,
                [new("STACK"), new("TABWELD")],
                [new("StartStep", ExecutionState.Scheduled, ExecutionState.Running),
                    new("CompleteStep", ExecutionState.Running, ExecutionState.Completed),
                    new("StartStep", ExecutionState.Completed, ExecutionState.Running)]);
            return Task.FromResult<UnitRouting?>(route);
        }

        public Task<UnitGuardSnapshot> ReadAsync(string siteId, string serialNumber, string actorId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UnitGuardSnapshot(Quality, LocationState.AtStation,
                ImmutableHashSet<string>.Empty));

        public Task<SerialReservationOutcome> ReserveAsync(string siteId, string serialNumber,
            Guid eventId, CancellationToken cancellationToken) =>
            Task.FromResult(_reserved.Add(siteId + ":" + serialNumber)
                ? SerialReservationOutcome.Reserved : SerialReservationOutcome.Duplicate);

        public Task RecordAsync(string siteId, string serialNumber, string submissionId,
            Guid eventId, string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        {
            Incidents++;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryEvents : IEventStore
    {
        private readonly Dictionary<(string, string), EventStream> _streams = new();
        private readonly Dictionary<(string, string), EventSnapshot> _snapshots = new();
        private long _sequence;

        public Task<long> AppendAsync(string siteId, string streamId, string streamType,
            long expectedVersion, ImmutableArray<NewStreamEvent> events, CancellationToken cancellationToken)
        {
            _streams.TryGetValue((siteId, streamId), out var old);
            var version = old?.Version ?? 0;
            if (version != expectedVersion)
            {
                throw new InvalidOperationException("version conflict");
            }
            var builder = (old?.Events ?? ImmutableArray<StoredStreamEvent>.Empty).ToBuilder();
            foreach (var value in events)
            {
                builder.Add(new StoredStreamEvent(++_sequence, siteId, streamId, ++version,
                    value.SourceEventId, value.EventType, value.SchemaVersion, value.PayloadJson,
                    value.MetadataJson, value.OccurredAt, value.RecordedAt));
            }
            _streams[(siteId, streamId)] = new EventStream(siteId, streamId, streamType,
                version, builder.ToImmutable());
            return Task.FromResult(version);
        }

        public Task<EventStream?> ReadStreamAsync(string siteId, string streamId,
            CancellationToken cancellationToken)
        {
            _streams.TryGetValue((siteId, streamId), out var value);
            return Task.FromResult(value);
        }

        public Task<EventSnapshot?> ReadSnapshotAsync(string siteId, string streamId,
            CancellationToken cancellationToken)
        {
            _snapshots.TryGetValue((siteId, streamId), out var value);
            return Task.FromResult(value);
        }

        public Task SaveSnapshotAsync(EventSnapshot snapshot, CancellationToken cancellationToken)
        {
            _snapshots[(snapshot.SiteId, snapshot.StreamId)] = snapshot;
            return Task.CompletedTask;
        }
    }
}
