using Microsoft.Extensions.Time.Testing;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Decoding;
using Nvm.EdgeGateway.Sessions;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.UnitTests.Sparkplug;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class SparkplugMessageDecoderTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 8, 28, 9, 30, 0, TimeSpan.Zero);
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

    private const string BirthTopic =
        "spBv1.0/NOVAVOLT-NV1-FORMATION/DBIRTH/EDGE-F1/FORM-01-CH-0142";

    private const string DataTopic =
        "spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142";

    [Fact]
    public void Decode_BirthThenAliasOnlyData_JoinsTopicPayloadAndGatewayClock()
    {
        var clock = new FakeTimeProvider(ReceivedAt);
        var decoder = Decoder(new OneDeviceDirectory(Channel), clock);

        var birth = decoder.Decode(BirthTopic, SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        birth.ShouldNotBeNull();
        birth.SiteId.ShouldBe("NV1");
        birth.EquipmentPath.ShouldBe(Channel);
        birth.GatewayTimestamp.ShouldBe(ReceivedAt);
        birth.Readings.Length.ShouldBe(5);

        clock.Advance(TimeSpan.FromMilliseconds(75));
        var data = decoder.Decode(DataTopic, SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        data.ShouldNotBeNull();
        data.GatewayTimestamp.ShouldBe(ReceivedAt.AddMilliseconds(75));
        data.Readings.Length.ShouldBe(2);
        data.Readings.Select(reading => reading.MetricName).ShouldBe(
            ["Formation/Voltage", "Formation/Temperature"],
            ignoreOrder: false);

        // The device time came from the fixture and is not overwritten by the gateway stamp.
        data.Readings.ShouldAllBe(reading => reading.DeviceTimestamp != data.GatewayTimestamp);
    }

    [Fact]
    public void Decode_DataBeforeBirth_RefusesUnknownAlias()
    {
        var decoder = Decoder(new OneDeviceDirectory(Channel), new FakeTimeProvider(ReceivedAt));

        Should.Throw<UnknownMetricAliasException>(() =>
            decoder.Decode(DataTopic, SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData)));
    }

    [Fact]
    public void Decode_TopicOutsideActiveModel_RejectsAtServerBoundary()
    {
        var decoder = Decoder(new OneDeviceDirectory(knownDevice: null), new FakeTimeProvider(ReceivedAt));

        var thrown = Should.Throw<UnknownEquipmentTopicException>(() =>
            decoder.Decode(BirthTopic, SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth)));

        thrown.Topic.ShouldBe(BirthTopic);
    }

    [Fact]
    public void Decode_HostStateTopic_IsIgnoredRatherThanReportedMalformed()
    {
        var decoder = Decoder(new OneDeviceDirectory(Channel), new FakeTimeProvider(ReceivedAt));

        decoder.Decode("spBv1.0/STATE/scada-primary", []).ShouldBeNull();
    }

    private static SparkplugMessageDecoder Decoder(IEquipmentDirectory directory, TimeProvider clock) =>
        new(new NodeSessionTracker(new GatewayCounters(), clock), directory, clock);

    private sealed class OneDeviceDirectory(EquipmentPath? knownDevice) : IEquipmentDirectory
    {
        public bool Contains(EquipmentPath path) =>
            knownDevice is not null
            && path.Kind == FactoryNodeKind.Line
            && string.Equals(path.Value, "NOVAVOLT/NV1/FORMATION/F1", StringComparison.Ordinal);

        public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode) =>
            knownDevice is not null
            && string.Equals(line.Value, "NOVAVOLT/NV1/FORMATION/F1", StringComparison.Ordinal)
            && string.Equals(deviceCode, knownDevice.Code, StringComparison.Ordinal)
                ? knownDevice
                : null;
    }
}
