using Nvm.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Which Sparkplug datatype becomes which <see cref="MetricValue"/>.</summary>
/// <remarks>
/// Payloads are built in-process here rather than captured — see <see cref="SparkplugPayloads"/> for
/// why that is the right trade for this particular question and the wrong one for the schema itself.
/// </remarks>
public sealed class SparkplugValueTests
{
    [Fact]
    public void TheFourScalarShapesAllArriveWithTheirValues()
    {
        var readings = SparkplugPayload.DecodeData(
            SparkplugPayloads.Encode(
                SparkplugPayloads.DefaultTimestampMs,
                seq: 1,
                Named("Formation/Pressure", DataType.Double, metric => metric.DoubleValue = 101.325),
                Named("Formation/CycleCount", DataType.Int64, metric => metric.LongValue = 4_294_967_296),
                Named("Formation/DoorClosed", DataType.Boolean, metric => metric.BooleanValue = true),
                Named("Formation/CellSerial", DataType.String, metric => metric.StringValue = "NV1CL16238A00123")),
            MetricAliasTable.Empty);

        readings.Select(reading => reading.Value).ShouldBe(
        [
            new MetricValue.Real(101.325),
            new MetricValue.Integral(4_294_967_296),
            new MetricValue.Flag(true),
            new MetricValue.Text("NV1CL16238A00123"),
        ]);
    }

    [Fact]
    public void AFloatIsWidenedToDoubleWithoutLosingAnything()
    {
        // Widened because ts.process_signal.value is DOUBLE PRECISION (docs/scope.md §8.3), and the
        // widening from binary32 is exact in a way the narrowing back would not be.
        Decode(Named("Formation/Voltage", DataType.Float, metric => metric.FloatValue = 3.6875f))
            .Value.ShouldBe(new MetricValue.Real(3.6875));
    }

    [Fact]
    public void ASignedIntegerKeepsTheSignItsDeclarationGivesIt()
    {
        // Int8, Int16 and Int32 all travel in int_value, which is a protobuf uint32, so -1 arrives on
        // the wire as 4294967295. Only the declared datatype separates the two readings below, and
        // that declaration comes from the birth — a second reason an unknown alias cannot be guessed.
        const uint OnTheWire = 4_294_967_295;

        Decode(Named("Formation/Trim", DataType.Int32, metric => metric.IntValue = OnTheWire))
            .Value.ShouldBe(new MetricValue.Integral(-1));

        Decode(Named("Formation/Counter", DataType.Uint32, metric => metric.IntValue = OnTheWire))
            .Value.ShouldBe(new MetricValue.Integral(4_294_967_295));

        Decode(Named("Formation/Offset", DataType.Int64, metric => metric.LongValue = ulong.MaxValue))
            .Value.ShouldBe(new MetricValue.Integral(-1));
    }

    [Fact]
    public void AnAliasedIntegerTakesItsSignFromTheBirthAndNotFromTheWire()
    {
        // Why the alias table carries the datatype and not only the name. A DDATA metric sends the
        // alias and the bytes and nothing else; 4294967295 is a perfectly good UInt32 and a perfectly
        // good -1, and the birth is the only place that ever said which. A table that remembered names
        // alone would read a trim of -1 as four billion, in a column that accepts it.
        var birth = SparkplugPayload.DecodeBirth(SparkplugPayloads.Encode(
            SparkplugPayloads.DefaultTimestampMs,
            seq: 0,
            new SparkplugMetric
            {
                Name = "Formation/Trim",
                Alias = 7,
                Datatype = (uint)DataType.Int32,
                Timestamp = SparkplugPayloads.DefaultTimestampMs,
                IntValue = 0,
            }));

        var update = new SparkplugMetric
        {
            Alias = 7,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            IntValue = 4_294_967_295,
        };

        var reading = SparkplugPayload
            .DecodeData(SparkplugPayloads.Carrying(update), birth.Aliases)
            .Single();

        reading.MetricName.ShouldBe("Formation/Trim");
        reading.Value.ShouldBe(new MetricValue.Integral(-1));
    }

    [Fact]
    public void AnUnsignedSixtyFourBitValueTooLargeToRepresentIsRefused()
    {
        // The one place the Integral case cannot hold what the wire can. Refused rather than wrapped
        // into a negative number, which would be a reading that is not merely wrong but plausible.
        Should.Throw<SparkplugDecodeException>(() =>
            Decode(Named("Formation/Ticks", DataType.Uint64, metric => metric.LongValue = ulong.MaxValue)));
    }

    [Fact]
    public void ADeviceThatSaysItHasNoValueIsRecordedAsSayingSo()
    {
        // A thermocouple that has come loose sends is_null. Reading that as 0 °C puts a plausible
        // number into a traceability record; dropping the metric makes it indistinguishable from
        // report-by-exception deciding nothing had changed. Both turn a known unknown into a fact.
        var metric = Named("Formation/Temperature", DataType.Float, _ => { });
        metric.IsNull = true;

        Decode(metric).Value.ShouldBe(MetricValue.Absent.Instance);
    }

    [Fact]
    public void AMetricWithNeitherAValueNorIsNullIsRefused()
    {
        // "No value" and "explicitly null" are different statements, and this payload makes neither.
        Should.Throw<SparkplugDecodeException>(() =>
            Decode(Named("Formation/Voltage", DataType.Float, _ => { })));
    }

    [Fact]
    public void ADeclarationThatContradictsTheWireIsRefused()
    {
        // The birth said Float and the payload carried text. Taking either side would be a guess about
        // which of the two is the mistake, and the guess would be recorded as a measurement.
        var thrown = Should.Throw<SparkplugDecodeException>(() =>
            Decode(Named("Formation/Voltage", DataType.Float, metric => metric.StringValue = "3.6875")));

        thrown.Message.ShouldContain("Formation/Voltage");
    }

    [Fact]
    public void ADatatypeThisPipelineDoesNotReadIsRefusedRatherThanSkipped()
    {
        // DataSet, Template, Bytes, File, the arrays. A formation channel emits none of them, so one
        // arriving means the payload is not what this pipeline thinks it is — which is worth stopping
        // on, not worth quietly dropping a metric over.
        var metric = Named("Formation/Curve", DataType.DataSet, _ => { });
        metric.DatasetValue = new Payload.Types.DataSet();

        Should.Throw<SparkplugDecodeException>(() => Decode(metric));
    }

    private static SparkplugMetric Named(string name, DataType datatype, Action<SparkplugMetric> value)
    {
        var metric = new SparkplugMetric
        {
            Name = name,
            Datatype = (uint)datatype,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
        };

        value(metric);

        return metric;
    }

    private static DeviceReading Decode(SparkplugMetric metric) =>
        SparkplugPayload.DecodeData(SparkplugPayloads.Carrying(metric), MetricAliasTable.Empty).Single();
}
