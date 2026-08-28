using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Reporting;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Simulator;

/// <summary>The three ways this plant misbehaves on purpose, and what each one must not change.</summary>
/// <remarks>
/// M2 exists to survive equipment that sends badly. A simulator that only sends well proves that the
/// happy path works, which nobody doubted — so these faults are the input the whole milestone is
/// measured against, and getting one of them subtly wrong makes every later number agree with itself
/// while measuring nothing.
/// </remarks>
public sealed class SimulatorFaultTests
{
    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

    private static readonly ImmutableArray<EquipmentPath> Channels =
    [
        .. Enumerable.Range(1, 8).Select(number =>
            EquipmentPath.Parse($"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-{number:0000}")),
    ];

    private static readonly DateTimeOffset StartedAt = new(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan SamplePeriod = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task OneMessageInTenIsSentASecondTime()
    {
        var messages = LogicalMessages(10_000);
        var run = await PublishAllAsync(messages, new SimulatorFaults { DuplicateRate = 0.10 });

        // Roughly a tenth, not exactly: the fault rolls dice, and a rate that came out exactly right
        // every time would be a schedule rather than a fault.
        run.Publisher.DuplicateMessages.ShouldBeInRange(900, 1_100);

        // The logical side is untouched. Ten thousand measurements were offered and ten thousand were
        // offered — the extra traffic is the link repeating itself, not the plant measuring more.
        run.Recorder.Count.ShouldBe(10_000 + (int)run.Publisher.DuplicateMessages);
        run.Publisher.PublishedMessages.ShouldBe(run.Recorder.Count);

        // Every extra message is the one before it, byte for byte. This is the assertion that decides
        // whether the fault is a duplicate at all: a message built afresh from the same readings would
        // carry a new seq and a new device_timestamp, deduplication would be right to keep it, and D1
        // would come out even having deduplicated nothing (R-M2-1).
        var repeats = run.Recorder.Messages
            .Zip(run.Recorder.Messages.Skip(1))
            .Count(pair => pair.First == pair.Second);

        repeats.ShouldBe((int)run.Publisher.DuplicateMessages);
    }

    [Fact]
    public async Task ADuplicateCarriesTheSameSourceEventIdAsTheOriginal()
    {
        var line = NewLine();
        var births = line.Connect(TimeSpan.Zero);
        var aliases = AliasesByDevice(births);

        var run = await PublishAllAsync(
            Advance(line, 400),
            new SimulatorFaults { DuplicateRate = 0.10 });

        var received = run.Recorder.Messages.ToArray();

        var repeated = Enumerable.Range(1, received.Length - 1)
            .First(index =>
                received[index] == received[index - 1]
                && received[index].Topic.MessageType == SparkplugMessageType.DeviceData);

        var original = received[repeated - 1];
        var again = received[repeated];

        var path = Channels.Single(channel => channel.Code == original.Topic.DeviceCode);
        var table = aliases[original.Topic.DeviceCode!];

        // Decoded independently, the way ingestion will decode them — one now and one after a gateway
        // has held it for three hours. Same identity both times is what makes ON CONFLICT DO NOTHING
        // at C12 collapse them into a single row.
        var first = Identities(SparkplugPayload.DecodeData(original.Payload.AsSpan(), table), path);
        var second = Identities(SparkplugPayload.DecodeData(again.Payload.AsSpan(), table), path);

        first.ShouldNotBeEmpty();
        second.ShouldBe(first);

        // And that is not vacuous: the same signal on the same channel read one second later gets a
        // different identity. Equality above really says "the same measurement", not "the key ignores
        // time" — which is the mistake lab C13.2 exists to price.
        var readings = SparkplugPayload.DecodeData(original.Payload.AsSpan(), table);
        var later = readings[0] with { DeviceTimestamp = readings[0].DeviceTimestamp.AddSeconds(1) };

        later.NaturalKey(path).SourceEventId.Value.ShouldNotBe(first[0]);
    }

    [Fact]
    public void AWrongClockMovesTheTimestampAndNothingElse()
    {
        var straight = NewLine();
        var drifted = NewLine(new DeviceClockDrift(1.0, TimeSpan.FromHours(2)));

        var right = straight.Connect(TimeSpan.Zero);
        var wrong = drifted.Connect(TimeSpan.Zero);

        drifted.DriftedDeviceCount.ShouldBe(Channels.Length);

        // The node birth is untouched. The drift is a device's front panel; an edge node is a
        // different box, and stamping bdSeq with a device's error would put C11's session matching out
        // for a fault that never touched the node.
        wrong[0].ShouldBe(right[0]);

        for (var index = 1; index < right.Length; index++)
        {
            var correct = SparkplugPayload.DecodeBirth(right[index].Payload.AsSpan()).Readings;
            var late = SparkplugPayload.DecodeBirth(wrong[index].Payload.AsSpan()).Readings;

            // Same metrics, same values, same cell serial. A dead CMOS battery does not re-engrave the
            // cell sitting in the channel, and a fault that changed the serial too would be simulating
            // a mislabelled cell — a different failure, and a much louder one.
            late.Select(reading => reading.MetricName).ShouldBe(correct.Select(reading => reading.MetricName));
            late.Select(reading => reading.Value).ShouldBe(correct.Select(reading => reading.Value));

            late.Select(reading => reading.DeviceTimestamp)
                .Zip(correct.Select(reading => reading.DeviceTimestamp))
                .ShouldAllBe(pair => (pair.First - pair.Second).Duration() == TimeSpan.FromHours(2));
        }
    }

    [Fact]
    public void TheSameDevicesAreWrongOnEveryRunAndTheyLeanBothWays()
    {
        var drift = new DeviceClockDrift(0.10, TimeSpan.FromHours(2));

        var codes = Enumerable.Range(1, 1_000).Select(number => $"FORM-01-CH-{number:0000}").ToArray();
        var wrong = codes.Where(code => drift.For(code) != TimeSpan.Zero).ToArray();

        // About a tenth of a real line's thousand channels. Not exactly a tenth — the choice comes out
        // of a hash — but close enough that a drift count is worth reading.
        wrong.Length.ShouldBeInRange(70, 130);

        // Both directions occur. A device can be ahead of the gateway as well as behind it, and code
        // that only ever met a late clock would be free to assume the difference is signed one way.
        wrong.Select(drift.For).Distinct().Order().ShouldBe([TimeSpan.FromHours(-2), TimeSpan.FromHours(2)]);

        // Fixed values, and this is what makes them worth writing down: string.GetHashCode is
        // randomised per process, so a selection built on it would pass every assertion above and
        // still hand the fault to different channels tomorrow. A run could then never be repeated.
        var sample = Enumerable.Range(1, 20).Select(number => $"FORM-01-CH-{number:0000}").ToArray();

        sample.Where(code => drift.For(code) != TimeSpan.Zero)
            .ShouldBe(["FORM-01-CH-0001", "FORM-01-CH-0003", "FORM-01-CH-0010", "FORM-01-CH-0015"]);

        drift.For("FORM-01-CH-0003").ShouldBe(TimeSpan.FromHours(-2));
        drift.For("FORM-01-CH-0010").ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task ADropoutIsAGapFollowedByABurstAndNotALoss()
    {
        var time = new FakeTimeProvider(StartedAt);
        var recorder = new RecordingPublisher();

        var publisher = new FaultInjectingPublisher(
            recorder,
            new SimulatorFaults
            {
                DropoutMeanInterval = TimeSpan.FromSeconds(10),
                DropoutDuration = TimeSpan.FromSeconds(30),
            },
            time,
            NullLogger<FaultInjectingPublisher>.Instance);

        await publisher.ConnectAsync(CancellationToken.None);

        var messages = LogicalMessages(200);
        var arrivals = new List<int>(messages.Count);
        var previous = 0;

        foreach (var message in messages)
        {
            await publisher.PublishAsync(message, CancellationToken.None);

            arrivals.Add(recorder.Count - previous);
            previous = recorder.Count;

            time.Advance(TimeSpan.FromSeconds(1));
        }

        await publisher.FlushAsync(CancellationToken.None);

        publisher.Dropouts.ShouldBeGreaterThan(0);
        publisher.HeldHighWater.ShouldBeGreaterThan(1);

        // Nothing arrives while the link is down...
        arrivals.ShouldContain(0);

        // ...and then the whole backlog arrives on one publish. That burst is what C10's rate limit
        // exists to survive; a fault that released the backlog gently would leave it nothing to prove.
        arrivals.ShouldContain(count => count > 1);

        // A gap, not a loss. Everything offered arrived, which is why a run that ends mid-dropout has
        // to flush before it writes its final count.
        recorder.Count.ShouldBe(messages.Count);
    }

    [Fact]
    public async Task TheRunReportSeparatesWhatWasMeasuredFromWhatWasSent()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json");

        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            SamplePeriod = SamplePeriod,
            TimeCompression = 1000,
            ReportPath = reportPath,
            Faults = { DuplicateRate = 0.5, DriftedDeviceRate = 1.0 },
        };

        var time = new FakeTimeProvider(StartedAt);
        var recorder = new RecordingPublisher();
        var line = NewLine(new DeviceClockDrift(options.Faults.DriftedDeviceRate, options.Faults.ClockDrift));

        var publisher = new FaultInjectingPublisher(
            recorder,
            options.Faults,
            time,
            NullLogger<FaultInjectingPublisher>.Instance);

        var worker = new SimulatorWorker(line, publisher, options, time, NullLogger<SimulatorWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => worker.IsRunning, "The simulator never armed its tick loop.");

        const int Ticks = 24;

        for (var tick = 1; tick <= Ticks; tick++)
        {
            time.Advance(options.TickInterval);

            await Eventually.TrueAsync(
                () => worker.ProcessElapsed >= options.SamplePeriod * tick,
                "The simulator did not advance. The tick loop is stuck.");
        }

        await worker.StopAsync(CancellationToken.None);

        var report = RunReportFile.Read(reportPath);
        File.Delete(reportPath);

        // ★ The left-hand side of D1. Signals the channels actually took — not messages, not
        // publishes, and unmoved by either fault.
        report.LogicalMeasurements.ShouldBe(line.MeasurementCount);
        report.LogicalMeasurements.ShouldBeGreaterThan(0);

        // The right-hand side of the same subtraction, kept apart from it. What the broker heard is
        // larger, and the difference is stated rather than left to be worked out.
        report.LogicalMessages.ShouldBe(worker.LogicalMessageCount);
        report.DuplicateMessages.ShouldBeGreaterThan(0);
        report.PublishedMessages.ShouldBe(report.LogicalMessages + report.DuplicateMessages);
        report.PublishedMessages.ShouldBe(recorder.Count);

        // Written down beside the totals so that a reconciliation that came out even cannot be read
        // as proof of deduplication when nothing was ever deduplicated (R-M2-1).
        report.FaultsEnabled.ShouldBeTrue();
        report.DriftedDevices.ShouldBe(Channels.Length);
        report.Channels.ShouldBe(Channels.Length);
        report.ProcessElapsed.ShouldBe(SamplePeriod * Ticks);
    }

    [Fact]
    public void SettingsThatDoNotDescribeAFaultAreRefused()
    {
        Should.Throw<InvalidOperationException>(() => new SimulatorFaults { DuplicateRate = 1.5 }.Validate());
        Should.Throw<InvalidOperationException>(() => new SimulatorFaults { DriftedDeviceRate = -0.1 }.Validate());

        // A dropout that lasts no time is a dropout nothing can observe, so it would sit in the
        // configuration looking switched on and do nothing at all.
        Should.Throw<InvalidOperationException>(() => new SimulatorFaults
        {
            DropoutMeanInterval = TimeSpan.FromSeconds(10),
            DropoutDuration = TimeSpan.Zero,
        }.Validate());

        Should.Throw<ArgumentOutOfRangeException>(() => new DeviceClockDrift(1.5, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void NoFaultsConfiguredMeansTheInjectorIsATunnel()
    {
        // The baseline every faulted run is compared against. If the injector were not transparent
        // with everything off, the comparison would be measuring the injector.
        new SimulatorFaults().AnyEnabled.ShouldBeFalse();
        new SimulatorFaults { DuplicateRate = 0.1 }.AnyEnabled.ShouldBeTrue();
        new SimulatorFaults { DriftedDeviceRate = 0.1 }.AnyEnabled.ShouldBeTrue();
        new SimulatorFaults { DropoutMeanInterval = TimeSpan.FromSeconds(1) }.AnyEnabled.ShouldBeTrue();

        DeviceClockDrift.None.For("FORM-01-CH-0003").ShouldBe(TimeSpan.Zero);
        NewLine().DriftedDeviceCount.ShouldBe(0);
    }

    private static Guid[] Identities(IEnumerable<DeviceReading> readings, EquipmentPath path) =>
        [.. readings.Select(reading => reading.NaturalKey(path).SourceEventId.Value)];

    private static FormationLine NewLine(DeviceClockDrift? drift = null) =>
        new(LinePath, Channels, FormationProfile.Default, StartedAt, 0, drift);

    // Unwrapped to the wire message on purpose. These tests drive the PUBLISHER, not the worker, so
    // nothing here confirms anything back to the line - and taking ComposedMessage would invite a
    // reader to think the run report is being kept up to date when it is not.
    private static List<SparkplugMessage> LogicalMessages(int count)
    {
        var line = NewLine();
        var messages = new List<SparkplugMessage>(line.Connect(TimeSpan.Zero).Select(composed => composed.Message));

        for (var elapsed = SamplePeriod; messages.Count < count; elapsed += SamplePeriod)
        {
            messages.AddRange(line.Advance(elapsed).Select(composed => composed.Message));
        }

        return [.. messages.Take(count)];
    }

    private static List<SparkplugMessage> Advance(FormationLine line, int ticks)
    {
        var messages = new List<SparkplugMessage>();

        for (var tick = 1; tick <= ticks; tick++)
        {
            messages.AddRange(line.Advance(SamplePeriod * tick).Select(composed => composed.Message));
        }

        return messages;
    }

    private static Dictionary<string, MetricAliasTable> AliasesByDevice(IEnumerable<ComposedMessage> births) =>
        births
            .Where(message => message.Topic.MessageType == SparkplugMessageType.DeviceBirth)
            .ToDictionary(
                message => message.Topic.DeviceCode!,
                message => SparkplugPayload.DecodeBirth(message.Payload.AsSpan()).Aliases,
                StringComparer.Ordinal);

    private static async Task<(RecordingPublisher Recorder, FaultInjectingPublisher Publisher)> PublishAllAsync(
        IEnumerable<SparkplugMessage> messages,
        SimulatorFaults faults)
    {
        var recorder = new RecordingPublisher();

        var publisher = new FaultInjectingPublisher(
            recorder,
            faults,
            new FakeTimeProvider(StartedAt),
            NullLogger<FaultInjectingPublisher>.Instance);

        await publisher.ConnectAsync(CancellationToken.None);

        foreach (var message in messages)
        {
            await publisher.PublishAsync(message, CancellationToken.None);
        }

        return (recorder, publisher);
    }
}
