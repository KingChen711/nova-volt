using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Simulator.Formation;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Simulator;

/// <summary>What the line publishes, and the property that makes time compression honest.</summary>
public sealed class FormationLineTests
{
    // Voltage, current, temperature, capacity, step and the cell serial. The serial is one of them:
    // the gateway forwards it and the pipeline stores a row for it like any other declared metric.
    private const int ReadingsPerDeclaration = 6;

    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

    private static readonly ImmutableArray<EquipmentPath> Channels =
    [
        .. Enumerable.Range(1, 4).Select(number =>
            EquipmentPath.Parse($"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-{number:0000}")),
    ];

    private static readonly DateTimeOffset StartedAt = new(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComingOnlineIsANodeBirthFollowedByADeviceBirthPerChannel()
    {
        var messages = Line().Connect(TimeSpan.Zero);

        messages.Length.ShouldBe(Channels.Length + 1);
        messages[0].Topic.MessageType.ShouldBe(SparkplugMessageType.NodeBirth);
        messages[0].Topic.DeviceCode.ShouldBeNull();

        // The order is not decoration: a device birth arriving before its node's would belong to a
        // session the consumer has never heard of, which is what C11 asks for a rebirth over.
        messages.Skip(1).ShouldAllBe(message => message.Topic.MessageType == SparkplugMessageType.DeviceBirth);

        messages.Skip(1).Select(message => message.Topic.DeviceCode)
            .ShouldBe(Channels.Select(channel => channel.Code));
    }

    [Fact]
    public void TheNodeBirthCarriesBdSeqWithoutAnAlias()
    {
        // bdSeq has to be readable in an NDEATH the broker publishes as a last will, and a last will
        // is composed before the birth that would have assigned an alias. Giving it one would make the
        // death certificate unreadable — and a death nobody can read is a node that looks alive.
        var birth = SparkplugPayload.DecodeBirth(Line(birthDeathSequence: 7).Connect(TimeSpan.Zero)[0].Payload.AsSpan());

        birth.Readings.Select(reading => reading.MetricName).ShouldBe(["bdSeq", "Node Control/Rebirth"]);
        birth.Readings.ShouldAllBe(reading => reading.Alias == null);
        birth.Readings[0].Value.ShouldBe(new MetricValue.Integral(7));
        birth.Aliases.Count.ShouldBe(0);
    }

    [Fact]
    public void ADeviceBirthDeclaresEveryMetricAndTheDataThatFollowsUsesTheAliases()
    {
        var line = Line();
        var birthMessages = line.Connect(TimeSpan.Zero);

        var birth = SparkplugPayload.DecodeBirth(birthMessages[1].Payload.AsSpan());

        birth.Readings.Select(reading => reading.MetricName).ShouldBe(
        [
            "Formation/Voltage",
            "Formation/Current",
            "Formation/Temperature",
            "Formation/Capacity",
            "Formation/StepIndex",
            "Formation/CellSerial",
        ]);

        // The round trip that matters: what the simulator writes, the decoder reads — and the update
        // that follows is unreadable without the birth, exactly as a real one is.
        var update = line.Advance(TimeSpan.FromMinutes(30))
            .First(message => message.Topic.DeviceCode == Channels[0].Code);

        var readings = SparkplugPayload.DecodeData(update.Payload.AsSpan(), birth.Aliases);

        readings.ShouldNotBeEmpty();
        readings.ShouldAllBe(reading => reading.MetricName.StartsWith("Formation/", StringComparison.Ordinal));
        Should.Throw<UnknownMetricAliasException>(
            () => SparkplugPayload.DecodeData(update.Payload.AsSpan(), MetricAliasTable.Empty));
    }

    [Fact]
    public void TheCellSerialIsOneAPlantCouldHaveEngraved()
    {
        var birth = SparkplugPayload.DecodeBirth(Line().Connect(TimeSpan.Zero)[1].Payload.AsSpan());

        var serial = birth.Readings.Single(reading => reading.MetricName == "Formation/CellSerial").Value;

        var text = serial.ShouldBeOfType<MetricValue.Text>().Value;

        SerialNumber.TryParse(text, out var parsed).ShouldBeTrue();
        parsed.SiteCode.ShouldBe("NV1");
        parsed.LineCode.ShouldBe("F1");
    }

    [Fact]
    public void ChannelsAreStaggeredAcrossTheCycleRatherThanStartedTogether()
    {
        // A line loads cells continuously, so at any moment some channels are charging and some are
        // resting. A line where all four stepped between stages at the same instant would produce a
        // traffic shape nothing downstream will ever see again.
        var line = Line();
        var births = line.Connect(TimeSpan.Zero);

        var steps = births
            .Skip(1)
            .Select(message => SparkplugPayload.DecodeBirth(message.Payload.AsSpan()))
            .Select(birth => birth.Readings.Single(reading => reading.MetricName == "Formation/StepIndex").Value)
            .Distinct()
            .Count();

        steps.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void ANewCellProducesAFreshDeviceBirth()
    {
        // A cycler publishes DBIRTH at the start of every cell: the serial has changed, and a birth is
        // the only message that carries names.
        var line = Line();

        line.Connect(TimeSpan.Zero);

        var oneCycleOn = line.Advance(FormationProfile.Default.CycleDuration);

        oneCycleOn.ShouldAllBe(message => message.Topic.MessageType == SparkplugMessageType.DeviceBirth);
        oneCycleOn.Length.ShouldBe(Channels.Length);
    }

    [Fact]
    public void ReportByExceptionMeansMostChannelsSayNothingMostOfTheTime()
    {
        // The traffic shape the whole protocol exists for. If every channel published on every sample
        // the aliases would be pointless and so would the deadbands.
        var line = Line();

        line.Connect(TimeSpan.Zero);

        var published = 0;
        var samples = 0;

        for (var elapsed = TimeSpan.FromSeconds(5); elapsed <= TimeSpan.FromHours(4); elapsed += TimeSpan.FromSeconds(5))
        {
            published += line.Advance(elapsed).Length;
            samples += Channels.Length;
        }

        published.ShouldBeLessThan(samples);
        published.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void RunningTheSameCycleTwiceProducesTheSameMeasurements()
    {
        // The property time compression rests on. Everything the line publishes is a function of
        // process time, so a run is reproducible — and a compressed run is the same run, finished
        // sooner. SimulatorWorkerTests is where the clock is actually compressed.
        RunOneCycle().ShouldBe(RunOneCycle());
    }

    [Fact]
    public void SamplingTwiceAsOftenMeasuresMoreRatherThanDifferently()
    {
        // Stated so that the invariant above is not mistaken for a wider one. The sample period is a
        // real decision about resolution; the compression factor is not.
        var coarse = RunOneCycle(TimeSpan.FromMinutes(10));
        var fine = RunOneCycle(TimeSpan.FromMinutes(5));

        fine.Measurements.ShouldBeGreaterThan(coarse.Measurements);
    }

    [Fact]
    public void AnEdgeNodeSpeaksForALineAndNotForAWorkCell()
    {
        Should.Throw<ArgumentException>(() => new FormationLine(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01"),
            Channels,
            FormationProfile.Default,
            StartedAt));
    }

    [Fact]
    public void ALineWithNoChannelsIsRefused()
    {
        // An empty list means the line was decommissioned or never rolled out, and a simulator that
        // started anyway would sit there publishing nothing and looking healthy.
        Should.Throw<ArgumentException>(() => new FormationLine(
            LinePath,
            [],
            FormationProfile.Default,
            StartedAt));
    }

    [Fact]
    public void TheCountIsEveryReadingTheChannelsTook()
    {
        // D1's left-hand side, pinned structurally. A birth declares six readings, and a hard-coded
        // "five" beside that array made the run report claim one fewer per DBIRTH — for every
        // channel, for the whole run. Nothing failed: the reconciliation simply came out short and
        // read as data loss. Counting the array cannot drift from the array.
        //
        // Counted here out of the payloads, the way the pipeline counts rows, so what is compared is
        // "what a consumer could store" against "what the plant says it measured" — the two sides D1
        // subtracts. Comparing the line's counter against a number derived from the same counter
        // would pass on any arithmetic at all.
        var line = Line();
        var ledger = new MeasurementLedger();

        ledger.AddAll(line.Connect(TimeSpan.Zero).Select(composed => composed.Message));

        for (var elapsed = TimeSpan.FromMinutes(5);
            elapsed <= FormationProfile.Default.CycleDuration;
            elapsed += TimeSpan.FromMinutes(5))
        {
            ledger.AddAll(line.Advance(elapsed).Select(composed => composed.Message));
        }

        ledger.Total.ShouldBe(line.MeasurementCount);
    }

    [Fact]
    public void ReadingTheChannelIsWhatMakesAMeasurement()
    {
        // J7, pinned the way round the plant works. The instrument has read the cell by the time
        // Advance returns: the deadband state has moved, so the next sample compares against a value
        // this one produced and the reading can never be taken again. Whether the message carrying it
        // then reaches a broker is the link's business, and a batch that is dropped afterwards is a
        // LOSS rather than a measurement that never happened.
        //
        // Counting on the far side of the publish instead looks stricter and is the opposite: the
        // readings would leave D1's left-hand side at the same moment the rows they owed failed to
        // arrive on the right, so the equality would hold across data the plant took and nobody has.
        // A 40-second broker outage would then reconcile exactly, and the formation curve of a cell
        // in a recalled lot would have a hole in it that no record calls a loss.
        var line = Line();

        line.Connect(TimeSpan.Zero);

        var afterBirth = line.MeasurementCount;

        afterBirth.ShouldBe(Channels.Length * ReadingsPerDeclaration);

        var dropped = line.Advance(TimeSpan.FromMinutes(30));

        // The batch is real and carries real readings — this is not a test of an empty tick.
        dropped.ShouldNotBeEmpty();
        dropped.Sum(message => message.Measurements).ShouldBeGreaterThan(0);

        // Composed, never published, and still measured. The count is the plant's, not the wire's.
        line.MeasurementCount.ShouldBe(afterBirth + dropped.Sum(message => message.Measurements));
    }

    [Fact]
    public void ARebirthRestatesAChannelRatherThanMeasuringItAgain()
    {
        // Same cell, same values, same device clock — therefore the same natural key, which the
        // database is right to store once. A second count here would put D1's left side above its
        // right by one full DBIRTH per channel per rebirth: measured at exactly -48 on an
        // eight-channel line. A restatement of a measurement is not another measurement.
        //
        // The line-level half of this. SimulatorWorkerTests drives the same property through the
        // worker, where the rebirth arrives on an MQTT thread instead of being called for.
        var line = Line();

        line.Connect(TimeSpan.Zero);

        var afterBirth = line.MeasurementCount;
        var rebirth = line.Connect(TimeSpan.Zero);

        rebirth.Length.ShouldBe(Channels.Length + 1);
        rebirth.Sum(message => message.Measurements).ShouldBe(0);
        line.MeasurementCount.ShouldBe(afterBirth);

        // And a rebirth is not a wall around the channel: the next real reading still counts.
        line.Advance(TimeSpan.FromMinutes(30)).Sum(message => message.Measurements).ShouldBeGreaterThan(0);
        line.MeasurementCount.ShouldBeGreaterThan(afterBirth);
    }

    private static FormationLine Line(ulong birthDeathSequence = 0) =>
        new(LinePath, Channels, FormationProfile.Default, StartedAt, birthDeathSequence);

    private static (int Messages, long Measurements) RunOneCycle(TimeSpan? samplePeriod = null)
    {
        var period = samplePeriod ?? TimeSpan.FromMinutes(5);
        var line = Line();
        var messages = line.Connect(TimeSpan.Zero).Length;

        for (var elapsed = period; elapsed <= FormationProfile.Default.CycleDuration; elapsed += period)
        {
            messages += line.Advance(elapsed).Length;
        }

        return (messages, line.MeasurementCount);
    }
}
