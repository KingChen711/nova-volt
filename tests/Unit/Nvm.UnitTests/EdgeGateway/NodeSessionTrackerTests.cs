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

        // STALE không phải là null và không phải là zero. Reading cuối cùng và thời điểm nó được đo
        // đều còn nguyên, vì "3,82 V lúc 07:15, và không còn đáng tin từ đó" mới là thứ khiến ai đó
        // đi kiểm tra mạng thay vì kiểm tra cell.
        var voltage = snapshot.Metrics.ShouldHaveSingleItem();
        voltage.MetricName.ShouldBe("Formation/Voltage");
        voltage.Liveness.ShouldBe(NodeLiveness.Stale);
        voltage.LastValue.ShouldBe(new MetricValue.Real(3.82f));
        voltage.LastDeviceTimestamp.ShouldNotBe(default);
    }

    [Fact]
    public void NodeDeath_ProducesNoReadingsToForward()
    {
        // Nửa cấu trúc của D4. Một death mà trả về reading sẽ tới được buffer, rồi tới ingestion, và
        // câu hỏi duy nhất còn lại sẽ là nó ghi gì vào đó.
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));

        harness.Decode(NodeDeathTopic, SparkplugPayloads.NodeDeath(birthDeathSequence: 6)).ShouldBeNull();
    }

    [Fact]
    public void LateNodeDeath_FromAReplacedSession_DoesNotKillTheLiveOne()
    {
        // Broker đã giữ will của session 6 trong khi node kết nối lại thành session 7. Nếu không có
        // phép so sánh bdSeq, một node đang publish ngay lúc này vẫn sẽ bị đánh dấu là đã chết.
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
        // Một DDEATH không mang bdSeq và chỉ đại diện cho một channel. Coi nó như một node death sẽ
        // để một cycler lỗi đánh dấu cả nghìn cycler đang khỏe mạnh thành stale.
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

        // Từ 5 tới 7. Một message đã bị thiếu, và rất có thể đó chính là message đánh số lại một alias.
        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 7, alias: 1, value: 3.9f));

        harness.Counters.RebirthRequests.ShouldBe(1);
        harness.Snapshot().SequenceGaps.ShouldBe(1);
        (await harness.Tracker.RebirthRequests.ReadAsync(TestContext.Current.CancellationToken))
            .ShouldBe(new NodeAddress(Line));

        // 7 trở thành baseline mới, nên 8 là đúng thứ tự và không yêu cầu thêm gì. Một tracker vẫn
        // cứ so sánh với 5 sẽ yêu cầu rebirth trên mọi message cho tới hết lần chạy.
        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 8, alias: 1, value: 4.0f));
        harness.Counters.RebirthRequests.ShouldBe(1);
    }

    [Fact]
    public void SequenceWrap_At255To0_IsNotAGap()
    {
        // Được lái qua tracker thay vì qua decoder: để đạt tới 255 một cách trung thực sẽ phải encode
        // 254 payload chỉ để assert một quy tắc về bộ đếm.
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

        // Session mới hoàn toàn có quyền gán 1 cho một metric khác. Giữ lại bảng cũ "phòng khi cần"
        // sẽ khiến reading tiếp theo bị ghi nhận dưới ý nghĩa cũ mà không dấy lên cảnh báo nào cả.
        Should.Throw<UnknownMetricAliasException>(() =>
            harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 1, alias: 1, value: 3.9f)));

        harness.Snapshot().BirthDeathSequence.ShouldBe(7ul);
        harness.Snapshot().Metrics.ShouldBeEmpty();
    }

    [Fact]
    public void ARedeliveredNodeBirth_KeepsTheAliasTableItAlreadyHas()
    {
        // MQTT là at-least-once và simulator cố tình tạo message trùng lặp, nên cùng một NBIRTH tới
        // hai lần với cùng bdSeq. Coi đó là một session mới sẽ xóa sạch một alias table đang hợp lệ
        // và khiến mọi message chỉ mang alias sau đó trở nên không đọc được — đo được hơn 10.000
        // message bị từ chối trong một lần chạy ngắn, trước khi có phép so sánh bdSeq.
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));
        harness.Decode(DeviceBirthTopic, SparkplugPayloads.BirthDeclaring(seq: 1, ("Formation/Voltage", 1)));

        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));

        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 2, alias: 1, value: 3.9f))
            .ShouldNotBeNull();
        harness.Snapshot().Liveness.ShouldBe(NodeLiveness.Online);
    }

    [Fact]
    public void ARedeliveredDataMessage_IsNotAGap()
    {
        // Message trùng lặp mang cùng seq như lần đầu. Một gap nghĩa là một message đã bị THIẾU, và
        // yêu cầu rebirth trên mọi message trùng lặp sẽ chôn vùi các gap thật sự trong nhiễu.
        var harness = new Harness();
        harness.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));
        harness.Decode(DeviceBirthTopic, SparkplugPayloads.BirthDeclaring(seq: 1, ("Formation/Voltage", 1)));

        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 2, alias: 1, value: 3.8f));
        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 2, alias: 1, value: 3.8f));
        harness.Decode(DeviceDataTopic, SparkplugPayloads.DataAt(seq: 3, alias: 1, value: 3.9f));

        harness.Counters.RebirthRequests.ShouldBe(0);
        harness.Snapshot().SequenceGaps.ShouldBe(0);
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
