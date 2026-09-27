using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class SparkplugIngressBatchCodecTests
{
    private static readonly DateTimeOffset DeviceAt = new(2026, 8, 28, 9, 15, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset GatewayAt = DeviceAt.AddMilliseconds(85);

    [Fact]
    public void RoundTrip_PreservesAddressBothTimestampsAliasesAndEveryScalarValue()
    {
        var original = Message(
            new DeviceReading("Formation/Voltage", 1, new MetricValue.Real(3.7125), DeviceAt),
            new DeviceReading("Formation/Cycle", 2, new MetricValue.Integral(7), DeviceAt.AddMilliseconds(1)),
            new DeviceReading("Formation/HeaterOn", 3, new MetricValue.Flag(true), DeviceAt.AddMilliseconds(2)),
            new DeviceReading("Formation/CellSerial", 4, new MetricValue.Text("NV1CL26001A00001"), DeviceAt),
            new DeviceReading("Formation/Temperature", null, MetricValue.Absent.Instance, DeviceAt));

        var bytes = SparkplugIngressBatchCodec.Encode([original]);
        var decoded = SparkplugIngressBatchCodec.Decode(bytes);

        decoded.ShouldHaveSingleItem().ShouldBe(original);
        decoded[0].GatewayTimestamp.ShouldBe(GatewayAt);
        decoded[0].Readings[0].DeviceTimestamp.ShouldBe(DeviceAt);
    }

    [Fact]
    public void RoundTrip_CarriesTheTraceParent_AndAMessageWithoutOneStaysWithout()
    {
        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var reading = new DeviceReading("Formation/Voltage", 1, new MetricValue.Real(3.7), DeviceAt);
        var traced = Message(reading) with { TraceParent = traceParent };
        var plain = Message(reading);

        var decoded = SparkplugIngressBatchCodec.Decode(SparkplugIngressBatchCodec.Encode([traced, plain]));

        decoded[0].TraceParent.ShouldBe(traceParent);
        decoded[1].TraceParent.ShouldBeNull();
        decoded[0].ShouldBe(plain);   // trace không phải dữ liệu nghiệp vụ: không ảnh hưởng so sánh bằng
    }

    [Theory]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", true)]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01", false)]   // trace id toàn 0 không hợp lệ
    [InlineData("not-a-trace", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TraceContextFromADevice_IsAcceptedOnlyInW3cFormat(string? value, bool accepted) =>
        (MqttTraceContext.Accept(value) is not null).ShouldBe(accepted);

    [Fact]
    public void Encode_EmptyBatch_RefusesARequestThatCouldAcknowledgeNothing()
    {
        Should.Throw<ArgumentException>(() =>
            SparkplugIngressBatchCodec.Encode(Array.Empty<DecodedSparkplugMessage>()));
    }

    [Fact]
    public void Decode_EmptyOrMalformedBody_ReportsContractFailure()
    {
        Should.Throw<SparkplugIngressBatchException>(() => SparkplugIngressBatchCodec.Decode([]));
        Should.Throw<SparkplugIngressBatchException>(() => SparkplugIngressBatchCodec.Decode([0xff, 0xff]));
    }

    [Fact]
    public void Message_SiteDisagreesWithTopicAndEquipment_RefusesCrossSiteObject()
    {
        var topic = SparkplugTopic.Parse(
            "spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142");
        var path = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

        Should.Throw<ArgumentException>(() =>
            new DecodedSparkplugMessage("DE1", path, topic, GatewayAt, ImmutableArray<DeviceReading>.Empty));
    }

    internal static DecodedSparkplugMessage Message(params DeviceReading[] readings)
    {
        var topic = SparkplugTopic.Parse(
            "spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142");
        var path = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

        return new DecodedSparkplugMessage("NV1", path, topic, GatewayAt, [.. readings]);
    }
}
