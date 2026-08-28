using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Reporting;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Simulator;

/// <summary>Compressing time must change how long a run takes and nothing else.</summary>
/// <remarks>
/// A formation cycle is eighteen hours and no test can wait eighteen hours — but a test that got
/// there by skipping samples would be measuring a different plant. The count of measurements is the
/// left-hand side of D1's reconciliation, so it has to survive the compression exactly.
/// </remarks>
public sealed class SimulatorWorkerTests
{
    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

    private static readonly EquipmentPath[] Channels =
    [
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001"),
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0002"),
    ];

    private static readonly DateTimeOffset StartedAt = new(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AThousandTimesFasterIsTheSameRun()
    {
        var slow = await RunOneCycleAsync(compression: 1);
        var fast = await RunOneCycleAsync(compression: 1000);

        // Identical plant.
        fast.Messages.ShouldBe(slow.Messages);
        fast.Measurements.ShouldBe(slow.Measurements);
        fast.ProcessElapsed.ShouldBe(slow.ProcessElapsed);
        fast.ProcessElapsed.ShouldBe(FormationProfile.Default.CycleDuration);

        // A thousandth of the clock. This is the only thing the compression is allowed to change, and
        // it is checked rather than assumed — a compression that quietly did nothing would make every
        // assertion above pass while an eighteen-hour test still took eighteen hours.
        slow.ClockElapsed.ShouldBe(FormationProfile.Default.CycleDuration);
        fast.ClockElapsed.ShouldBe(slow.ClockElapsed / 1000);
    }

    [Fact]
    public async Task EveryMessageOfARunNamesAPlaceOnThisLine()
    {
        var run = await RunOneCycleAsync(compression: 1000);

        run.Topics.ShouldAllBe(topic => topic.LinePath == LinePath);
        run.Topics.Where(topic => topic.DeviceCode is not null)
            .ShouldAllBe(topic => Channels.Any(channel => channel.Code == topic.DeviceCode));
    }

    [Fact]
    public async Task ARebirthRequest_MakesTheNodeDeclareItselfAgain()
    {
        // Without this the gateway is blind for the rest of the run. It subscribes a second after the
        // simulator published its births, cannot read one alias-only message afterwards, asks for a
        // rebirth on every gap — and if nothing answers, the next DBIRTH is a cell change away, which
        // on a formation line is eighteen hours. Measured before this existed: 10.000+ messages
        // rejected in a few minutes.
        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            SamplePeriod = TimeSpan.FromMinutes(30),
            TimeCompression = 1000,
            ReportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json"),
        };
        var line = new FormationLine(LinePath, Channels, FormationProfile.Default, StartedAt);
        var recorder = new RecordingPublisher();
        var time = new FakeTimeProvider(StartedAt);
        var publisher = new FaultInjectingPublisher(
            recorder,
            new SimulatorFaults(),
            time,
            NullLogger<FaultInjectingPublisher>.Instance);
        using var worker = new SimulatorWorker(line, publisher, options, time, NullLogger<SimulatorWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => worker.IsRunning, "The simulator never armed its tick loop.");

        var afterConnect = recorder.Messages.Count;
        var measurementsBeforeRebirth = line.MeasurementCount;
        recorder.RebirthRequested.ShouldNotBeNull();

        await recorder.RebirthRequested!(CancellationToken.None);

        // One NBIRTH and one DBIRTH per channel, exactly as at connect.
        (recorder.Messages.Count - afterConnect).ShouldBe(Channels.Length + 1);
        worker.Rebirths.ShouldBe(1);

        // Republished, not re-measured. The rebirth restates the same cell at the same instant, so
        // it carries the same natural key and deduplication stores it once. A run report that
        // counted it again would put the left side of D1 above the right by one full DBIRTH per
        // rebirth — measured at exactly -48 on an eight-channel line before this held.
        line.MeasurementCount.ShouldBe(measurementsBeforeRebirth);

        // A consumer that missed the births asks once per detected gap, so it asks thousands of times
        // before the first answer reaches it. Answering each would drown the data it wants to read.
        await recorder.RebirthRequested!(CancellationToken.None);
        worker.Rebirths.ShouldBe(1);

        await worker.StopAsync(CancellationToken.None);
        File.Delete(options.ReportPath);
    }

    [Fact]
    public async Task AGracefulStopFinishesTheBatchItHasAlreadyComposed()
    {
        // Where the plant is when a stop arrives: the channels have been read, the deadbands have
        // moved, and half the batch is still on its way out. Dropping the rest would be the cheapest
        // possible shutdown and it would put readings on D1's left-hand side that no database is
        // ever offered — the D3 lab stops the source on purpose, so it stands exactly here nearly
        // every run.
        //
        // The plant stops between TICKS instead, where nothing has been measured yet and stopping
        // costs nothing.
        var rig = Rig();

        await rig.Worker.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Worker.IsRunning, "The simulator never armed its tick loop.");

        // Stand inside the batch rather than racing for it: the first publish of the next tick is
        // held open until this test lets it go.
        rig.Link.CatchNext();
        rig.Time.Advance(rig.Options.TickInterval);

        await rig.Link.Caught;

        var caughtAt = rig.Link.Count;
        var stopping = rig.Worker.StopAsync(CancellationToken.None);

        rig.Link.Release();

        await stopping;

        // The message that was caught, plus at least one composed behind it that the cancel could
        // have taken. Without the second one this test would pass on an empty batch.
        (rig.Link.Count - caughtAt).ShouldBeGreaterThan(1);

        rig.Worker.AbandonedMeasurements.ShouldBe(0);
        rig.Worker.LogicalMessageCount.ShouldBe(rig.Link.Count);

        // Both sides, and neither derived from the other: what a consumer could store out of the
        // payloads, against what the plant says its channels took.
        var ledger = new MeasurementLedger();

        ledger.AddAll(rig.Link.Messages);
        ledger.Total.ShouldBe(rig.Line.MeasurementCount);

        var report = rig.ReadReport();

        report.LogicalMeasurements.ShouldBe(rig.Line.MeasurementCount);
        report.AbandonedMeasurements.ShouldBe(0);
    }

    [Fact]
    public async Task ALinkThatDiesMidBatchCannotMakeTheReconciliationComeOutEven()
    {
        // The other half of J7, and the one that decides whether the oracle can see anything at all.
        // A publish that throws leaves the rest of a composed batch on the floor: those readings were
        // taken, and a database will never be offered them.
        //
        // If the left-hand side were counted after the publish, it would drop by exactly the amount
        // the right-hand side is about to be short — and D1 would come out even over a plant that
        // lost data. What must happen instead is that the count stays, the difference shows, and the
        // gate goes red with a number saying which side of the wire the loss was on.
        var rig = Rig();

        await rig.Worker.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Worker.IsRunning, "The simulator never armed its tick loop.");

        // One more message gets through, and the rest of the batch behind it does not.
        rig.Link.FailAfter(rig.Link.Count + 1);
        rig.Time.Advance(rig.Options.TickInterval);

        await Eventually.TrueAsync(
            () => rig.Worker.AbandonedMeasurements > 0,
            "The dead link never cut into a batch, so this proves nothing.");

        await rig.Worker.StopAsync(CancellationToken.None);

        var ledger = new MeasurementLedger();

        ledger.AddAll(rig.Link.Messages);

        // Stated as an equation because that is what the gate has to be able to say: what the plant
        // measured, minus what the link carried, is the loss — reported, not netted off.
        (rig.Line.MeasurementCount - ledger.Total).ShouldBe(rig.Worker.AbandonedMeasurements);

        var report = rig.ReadReport();

        report.LogicalMeasurements.ShouldBe(rig.Line.MeasurementCount);
        report.AbandonedMeasurements.ShouldBe(rig.Worker.AbandonedMeasurements);

        // The false pass this exists to prevent. A database holding everything the link carried is
        // still short of the report, so the subtraction D1 runs cannot come out at zero.
        report.LogicalMeasurements.ShouldBeGreaterThan(ledger.Total);
    }

    [Fact]
    public void SettingsThatCannotProduceAPlantAreRefusedBeforeAnythingConnects()
    {
        // Every one of these would otherwise fail somewhere in the middle of a run, where the cause is
        // a great deal harder to see than it is here.
        Should.Throw<InvalidOperationException>(() => new SimulatorOptions { TimeCompression = 0 }.Validate());
        Should.Throw<InvalidOperationException>(() => new SimulatorOptions { SamplePeriod = TimeSpan.Zero }.Validate());

        // 18 hours is not a whole number of 7-minute samples, so the last sample of one cell would sit
        // closer to the first of the next than any other pair in the run.
        Should.Throw<InvalidOperationException>(
            () => new SimulatorOptions { SamplePeriod = TimeSpan.FromMinutes(7) }.Validate());

        // Below a millisecond a timer stops keeping up and the run becomes slower than the compression
        // claims — which would turn a throughput number into a lie.
        Should.Throw<InvalidOperationException>(
            () => new SimulatorOptions { SamplePeriod = TimeSpan.FromSeconds(1), TimeCompression = 5000 }.Validate());
    }

    // A worker with a link a test can stand inside, wired the way the simulator actually runs: the
    // fault injector is in the path with every rate at zero, because that pass-through is what a
    // faultless run goes through too.
    private static WorkerRig Rig()
    {
        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            SamplePeriod = TimeSpan.FromMinutes(30),
            TimeCompression = 1000,
            ReportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json"),
        };

        var time = new FakeTimeProvider(StartedAt);
        var link = new InterruptiblePublisher();
        var line = new FormationLine(LinePath, Channels, FormationProfile.Default, StartedAt);

        var publisher = new FaultInjectingPublisher(
            link,
            options.Faults,
            time,
            NullLogger<FaultInjectingPublisher>.Instance);

        return new WorkerRig(
            new SimulatorWorker(line, publisher, options, time, NullLogger<SimulatorWorker>.Instance),
            line,
            link,
            time,
            options);
    }

    private sealed record WorkerRig(
        SimulatorWorker Worker,
        FormationLine Line,
        InterruptiblePublisher Link,
        FakeTimeProvider Time,
        SimulatorOptions Options)
    {
        // Read from the file rather than from the worker. D1 and D3 read this file and nothing else,
        // so a counter that was right in memory and missing from the JSON would still leave both
        // labs comparing a number they never saw.
        public RunReport ReadReport()
        {
            var report = RunReportFile.Read(Options.ReportPath);

            File.Delete(Options.ReportPath);

            return report;
        }
    }

    private static async Task<RunResult> RunOneCycleAsync(double compression)
    {
        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            SamplePeriod = TimeSpan.FromMinutes(30),
            TimeCompression = compression,
            ReportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json"),
        };

        var time = new FakeTimeProvider(StartedAt);
        var recorder = new RecordingPublisher();
        var line = new FormationLine(LinePath, Channels, FormationProfile.Default, StartedAt);

        // Through the fault injector with every rate at zero, because that is the arrangement the
        // simulator actually runs in. Testing the worker against a bare publisher would leave the
        // pass-through untested exactly where it matters most: the run with no faults is the baseline
        // every faulted run is compared against.
        var publisher = new FaultInjectingPublisher(
            recorder,
            options.Faults,
            time,
            NullLogger<FaultInjectingPublisher>.Instance);

        var worker = new SimulatorWorker(line, publisher, options, time, NullLogger<SimulatorWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        // Advancing a fake clock before the tick loop is armed moves the plant's time past a tick that
        // nothing is holding, and the run silently comes up one sample short. Real time cannot deliver
        // a tick that early, so this is a hazard the fake clock introduces and the fake clock must
        // answer for.
        await Eventually.TrueAsync(() => worker.IsRunning, "The simulator never armed its tick loop.");

        var ticks = (int)(FormationProfile.Default.CycleDuration / options.SamplePeriod);

        for (var tick = 1; tick <= ticks; tick++)
        {
            time.Advance(options.TickInterval);

            await Eventually.TrueAsync(
                () => worker.ProcessElapsed >= options.SamplePeriod * tick,
                "The simulator did not advance. The tick loop is stuck.");
        }

        await worker.StopAsync(CancellationToken.None);

        File.Delete(options.ReportPath);

        return new RunResult(
            recorder.Messages.Count,
            line.MeasurementCount,
            worker.ProcessElapsed,
            time.GetUtcNow() - StartedAt,
            [.. recorder.Messages.Select(message => message.Topic)]);
    }

    private sealed record RunResult(
        int Messages,
        long Measurements,
        TimeSpan ProcessElapsed,
        TimeSpan ClockElapsed,
        IReadOnlyList<SparkplugTopic> Topics);
}
