using Microsoft.Extensions.Time.Testing;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Decoding;
using Nvm.EdgeGateway.Sessions;
using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.EdgeGateway;

/// <summary>
/// The topology `scope.md` §9/M2 promised and nothing had run: <b>1.000 formation channels under one
/// edge node</b>. The demo seed stays at eight because it is the file people read to understand the
/// plant, so the load topology is a separate catalog — see `deploy/seed-load/make-load-topology.py`.
/// </summary>
/// <remarks>
/// <para>
/// Cardinality here is ingestion's problem, not the simulator's. Sparkplug gives one edge node one
/// <c>seq</c> stream and one <c>bdSeq</c>, but every device under it gets <b>its own alias table</b>,
/// declared once in its <c>DBIRTH</c> and never restated. A thousand devices is a thousand tables
/// alive at once under a single session.
/// </para>
/// <para>
/// What goes wrong if they are not kept apart is silent and permanent for the session: alias 7 means
/// one metric on one channel and another metric on the next, so a shared table does not fail — it
/// attributes a temperature to a voltage and stores it. A reading nobody can tell is wrong is worse
/// than a reading that was refused.
/// </para>
/// </remarks>
public sealed class ThousandChannelTopologyTests
{
    private const string LoadSeed = "seed-load";
    private const string DemoSeed = "seed";

    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly DateTimeOffset ReceivedAt = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    private const ulong VoltageAlias = 1;
    private const ulong CapacityAlias = 7;

    [Fact]
    public void TheLoadTopologyModelsAThousandChannels_AndTheDemoSeedStillModelsEight()
    {
        // Both halves matter. A load fixture that quietly became the demo would put a thousand
        // hand-unreadable lines in front of the next person opening the factory model, and a demo
        // that quietly became the load fixture would leave D2 measuring eight channels again.
        ChannelsOf(LoadSeed).Length.ShouldBe(1_000);
        ChannelsOf(DemoSeed).Length.ShouldBe(8);
    }

    [Fact]
    public void TheLoadTopologySpreadsChannelsAcrossCyclers_TheWayALineIsBuilt()
    {
        // Ten cyclers of a hundred, not one cycler of a thousand. No cycler on any line has a
        // thousand channels, and the number of work cells is itself a dimension the gateway has to
        // resolve when it turns a topic into an equipment path.
        var channels = ChannelsOf(LoadSeed);
        var cyclers = channels
            .Select(channel => channel.Segments[4])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        cyclers.Length.ShouldBe(10);
        channels.Select(channel => channel.Value).Distinct(StringComparer.Ordinal).Count().ShouldBe(1_000);
    }

    [Fact]
    public void AThousandDevicesUnderOneEdgeNode_EachKeepTheirOwnAliasTable()
    {
        var clock = new FakeTimeProvider(ReceivedAt);
        var directory = SeededEquipmentDirectory.Load(LoadSeed, requestedRevision: null);
        var decoder = new SparkplugMessageDecoder(new NodeSessionTracker(new GatewayCounters(), clock), directory, clock);
        var channels = ChannelsOf(LoadSeed);

        var sequence = 0UL;

        // Null, and that is the correct answer: an NBIRTH carries bdSeq and the control metrics and
        // nothing a machine measured, so the session tracker consumes it and there is nothing to
        // forward. The session is open all the same, which is what the thousand births below need.
        decoder.Decode(Topic(Line, SparkplugMessageType.NodeBirth), NodeBirth(ref sequence)).ShouldBeNull();

        // Every channel declares voltage. Only the first also declares capacity, which is what makes
        // the tables provably separate a few lines further down.
        foreach (var channel in channels)
        {
            var declaresCapacity = channel == channels[0];
            var birth = decoder.Decode(
                Topic(channel, SparkplugMessageType.DeviceBirth),
                DeviceBirth(ref sequence, declaresCapacity));

            birth.ShouldNotBeNull();
            birth.EquipmentPath.ShouldBe(channel);
        }

        // Alias-only data on all thousand. This is the whole point of a birth: nothing in these
        // payloads says "voltage", and every one of them still resolves to it.
        foreach (var channel in channels)
        {
            var data = decoder.Decode(
                Topic(channel, SparkplugMessageType.DeviceData),
                AliasOnlyData(ref sequence, VoltageAlias));

            data.ShouldNotBeNull();
            data.Readings.ShouldHaveSingleItem().MetricName.ShouldBe("Formation/Voltage");
        }

        // The tables are per device and not one table shared by the node. Alias 7 was declared on
        // the first channel only, so it reads there and is refused everywhere else - and refusing is
        // the correct outcome, because the alternative is attributing one channel's metric to
        // another and storing it as if it had been measured.
        decoder.Decode(
                Topic(channels[0], SparkplugMessageType.DeviceData),
                AliasOnlyData(ref sequence, CapacityAlias))
            .ShouldNotBeNull()
            .Readings.ShouldHaveSingleItem()
            .MetricName.ShouldBe("Formation/Capacity");

        Should.Throw<UnknownMetricAliasException>(() => decoder.Decode(
            Topic(channels[1], SparkplugMessageType.DeviceData),
            AliasOnlyData(ref sequence, CapacityAlias)));
    }

    private static EquipmentPath[] ChannelsOf(string seedDirectory)
    {
        var catalog = FactoryModelSeed.LoadCatalog(seedDirectory);
        var snapshot = catalog.Find(catalog.LatestRevision)
            ?? throw new InvalidOperationException($"'{seedDirectory}' has no revision to read.");

        return [.. FormationChannels.Under(snapshot, Line)];
    }

    private static string Topic(EquipmentPath path, SparkplugMessageType messageType) =>
        SparkplugTopic.For(path, messageType).Value;

    // seq counts across the whole session, births and data alike, exactly as a real node numbers it.
    private static ulong Next(ref ulong sequence)
    {
        var current = sequence;
        sequence = (sequence + 1) % 256;

        return current;
    }

    private static byte[] NodeBirth(ref ulong sequence) =>
        SparkplugPayload.EncodeBirth(
            [
                new DeviceReading(
                    SparkplugPayload.BirthDeathSequenceMetric,
                    Alias: null,
                    new MetricValue.Integral(1),
                    ReceivedAt),
            ],
            Next(ref sequence),
            ReceivedAt);

    private static byte[] DeviceBirth(ref ulong sequence, bool declaresCapacity)
    {
        List<DeviceReading> readings =
        [
            new("Formation/Voltage", VoltageAlias, new MetricValue.Real(3.7), ReceivedAt),
        ];

        if (declaresCapacity)
        {
            readings.Add(new DeviceReading("Formation/Capacity", CapacityAlias, new MetricValue.Real(4.8), ReceivedAt));
        }

        return SparkplugPayload.EncodeBirth(readings, Next(ref sequence), ReceivedAt);
    }

    private static byte[] AliasOnlyData(ref ulong sequence, ulong alias) =>
        SparkplugPayload.EncodeData(
            [new DeviceReading(MetricName: string.Empty, alias, new MetricValue.Real(3.71), ReceivedAt)],
            Next(ref sequence),
            ReceivedAt);
}
