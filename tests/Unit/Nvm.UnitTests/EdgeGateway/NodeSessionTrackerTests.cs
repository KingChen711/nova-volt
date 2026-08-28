using Microsoft.Extensions.Time.Testing;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Decoding;
using Nvm.EdgeGateway.Sessions;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;
using Nvm.UnitTests.Sparkplug;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class NodeSessionTrackerTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 8, 28, 9, 30, 0, TimeSpan.Zero);
    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

    private const string NodeBirthTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/NBIRTH/EDGE-F1";
    private const string NodeDeathTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/NDEATH/EDGE-F1";
    private const string DeviceBirthTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/DBIRTH/EDGE-F1/FORM-01-CH-0142";
    private const string DeviceDataTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142";
    private const string DeviceDeathTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/DDEATH/EDGE-F1/FORM-01-CH-0142";

    [Fact]
    public void NodeDeath_MarksEveryMetricStaleAndKeepsWhatItLastSaid()
    {
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));
        harness.Decode(DeviceBirthTopic, SparkplugPayloads.BirthDeclaring(seq: 1, ("Formation/Voltage", 1)));
        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 2, alias: 1, value: 3.82f));

        harness.Decode(NodeDeathTopic, SparkplugPayloads.NodeDeath(birthDeathSequence: 6));

        var snapshot = harness.Snapshot();
        snapshot.Liveness.ShouldBe(NodeLiveness.Stale);
        snapshot.StaleSince.ShouldBe(ReceivedAt);
        harness.Tracker.StaleNodeCount.ShouldBe(1);

        // STALE is not null and not zero. The last reading and the moment it was taken both survive,
        // because "3,82 V at 07:15, and untrustworthy since" is what sends someone to check the
        // network instead of the cell.
        var voltage = snapshot.Metrics.ShouldHaveSingleItem();
        voltage.MetricName.ShouldBe("Formation/Voltage");
        voltage.Liveness.ShouldBe(NodeLiveness.Stale);
        voltage.LastValue.ShouldBe(new MetricValue.Real(3.82f));
        voltage.LastDeviceTimestamp.ShouldNotBe(default);
    }

    [Fact]
    public void NodeDeath_ProducesNoReadingsToForward()
    {
        // D4's structural half. A death that returned readings would reach the buffer, then
        // ingestion, and the only question left would be what it wrote there.
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));

        harness.Decode(NodeDeathTopic, SparkplugPayloads.NodeDeath(birthDeathSequence: 6)).ShouldBeNull();
    }

    [Fact]
    public void LateNodeDeath_FromAReplacedSession_DoesNotKillTheLiveOne()
    {
        // The broker held session 6's will while the node reconnected as session 7. Without the
        // bdSeq comparison, a node that is publishing right now would be marked dead.
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 7));

        harness.Decode(NodeDeathTopic, SparkplugPayloads.NodeDeath(birthDeathSequence: 6));

        harness.Snapshot().Liveness.ShouldBe(NodeLiveness.Online);
        harness.Snapshot().BirthDeathSequence.ShouldBe(7ul);
        harness.Counters.LateDeathsIgnored.ShouldBe(1);
    }

    [Fact]
    public void DeviceDeath_DoesNotEndTheNodeSession()
    {
        // A DDEATH carries no bdSeq and speaks for one channel. Treating it as a node death would
        // let one failing cycler mark a thousand healthy ones stale.
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));

        harness.Decode(DeviceDeathTopic, SparkplugPayloads.NodeDeath(birthDeathSequence: null));

        harness.Snapshot().Liveness.ShouldBe(NodeLiveness.Online);
        harness.Tracker.StaleNodeCount.ShouldBe(0);
    }

    [Fact]
    public void DataAfterADeath_PutsTheNodeBackOnline()
    {
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));
        harness.Decode(DeviceBirthTopic, SparkplugPayloads.BirthDeclaring(seq: 1, ("Formation/Voltage", 1)));
        harness.Decode(NodeDeathTopic, SparkplugPayloads.NodeDeath(birthDeathSequence: 6));

        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 2, alias: 1, value: 3.9f));

        var snapshot = harness.Snapshot();
        snapshot.Liveness.ShouldBe(NodeLiveness.Online);
        snapshot.StaleSince.ShouldBeNull();
        snapshot.Metrics.ShouldHaveSingleItem().Liveness.ShouldBe(NodeLiveness.Online);
    }

    [Fact]
    public async Task SequenceGap_AsksTheNodeToDeclareItselfAgain_Once()
    {
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));
        harness.Decode(DeviceBirthTopic, SparkplugPayloads.BirthDeclaring(seq: 1, ("Formation/Voltage", 1)));

        foreach (var seq in (ulong[])[2, 3, 4, 5])
        {
            harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq, alias: 1, value: 3.8f));
        }

        harness.Counters.RebirthRequests.ShouldBe(0);

        // 5 to 7. One message was missed, and it may have been the one that renumbered an alias.
        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 7, alias: 1, value: 3.9f));

        harness.Counters.RebirthRequests.ShouldBe(1);
        harness.Snapshot().SequenceGaps.ShouldBe(1);
        (await harness.Tracker.RebirthRequests.ReadAsync(TestContext.Current.CancellationToken))
            .ShouldBe(new NodeAddress(Line));

        // 7 became the new baseline, so 8 is in order and asks for nothing further. A tracker that
        // kept comparing against 5 would request a rebirth on every message for the rest of the run.
        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 8, alias: 1, value: 4.0f));
        harness.Counters.RebirthRequests.ShouldBe(1);
    }

    [Fact]
    public void SequenceWrap_At255To0_IsNotAGap()
    {
        // Driven through the tracker rather than the decoder: reaching 255 honestly would mean
        // encoding 254 payloads to assert one rule about the counter.
        var harness = new Harness();
        var topic = SparkplugTopic.Parse(DeviceDataTopic);

        harness.Tracker.ObserveData(topic, [], sequence: 254);
        harness.Tracker.ObserveData(topic, [], sequence: 255);
        harness.Tracker.ObserveData(topic, [], sequence: 0);
        harness.Tracker.ObserveData(topic, [], sequence: 1);

        harness.Counters.RebirthRequests.ShouldBe(0);
        harness.Snapshot().SequenceGaps.ShouldBe(0);
    }

    [Fact]
    public void NodeBirth_WithANewSession_AbandonsTheOldAliasTableCompletely()
    {
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));
        harness.Decode(DeviceBirthTopic, SparkplugPayloads.BirthDeclaring(seq: 1, ("Formation/Voltage", 1)));
        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 2, alias: 1, value: 3.8f))
            .ShouldNotBeNull();

        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 7));

        // The new session is free to give 1 to a different metric. Keeping the old table "just in
        // case" would file the next reading under the previous meaning and raise nothing at all.
        Should.Throw<UnknownMetricAliasException>(() =>
            harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 1, alias: 1, value: 3.9f)));

        harness.Snapshot().BirthDeathSequence.ShouldBe(7ul);
        harness.Snapshot().Metrics.ShouldBeEmpty();
    }

    [Fact]
    public void UnknownAlias_AsksForARebirthBeforeRefusingTheMessage()
    {
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));

        Should.Throw<UnknownMetricAliasException>(() =>
            harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 1, alias: 9, value: 3.9f)));

        harness.Counters.RebirthRequests.ShouldBe(1);
    }

    private sealed class Harness
    {
        internal Harness()
        {
            var clock = new FakeTimeProvider(ReceivedAt);
            Counters = new GatewayCounters();
            Tracker = new NodeSessionTracker(Counters, clock);
            Decoder = new SparkplugMessageDecoder(Tracker, new LineDirectory(), clock);
        }

        internal GatewayCounters Counters { get; }

        internal NodeSessionTracker Tracker { get; }

        internal SparkplugMessageDecoder Decoder { get; }

        internal DecodedSparkplugMessage? Decode(string topic, byte[] payload) =>
            Decoder.Decode(topic, payload);

        internal NodeSessionSnapshot Snapshot() => Tracker.Snapshot(new NodeAddress(Line));
    }

    private sealed class LineDirectory : IEquipmentDirectory
    {
        public bool Contains(EquipmentPath path) => path == Line;

        public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode) =>
            line == Line && string.Equals(deviceCode, Channel.Code, StringComparison.Ordinal)
                ? Channel
                : null;
    }
}
