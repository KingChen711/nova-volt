using Nvm.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Datatype Sparkplug nào trở thành <see cref="MetricValue"/> nào.</summary>
/// <remarks>
/// Payload được build in-process ở đây thay vì capture — xem <see cref="SparkplugPayloads"/> để biết
/// vì sao đó là trade phù hợp cho câu hỏi này nhưng sai cho chính schema.
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
        // Widen vì ts.process_signal.value là DOUBLE PRECISION (docs/scope.md §8.3), và widen từ
        // binary32 là chính xác tuyệt đối theo cách narrow ngược lại không làm được.
        Decode(Named("Formation/Voltage", DataType.Float, metric => metric.FloatValue = 3.6875f))
            .Value.ShouldBe(new MetricValue.Real(3.6875));
    }

    [Fact]
    public void ASignedIntegerKeepsTheSignItsDeclarationGivesIt()
    {
        // Int8, Int16 và Int32 đều truyền trong int_value, một protobuf uint32, nên -1 đến trên wire là
        // 4294967295. Chỉ datatype được khai báo phân biệt hai reading dưới đây, và khai báo đến từ
        // birth — lý do thứ hai không thể đoán alias không biết.
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
        // Lý do alias table mang datatype chứ không chỉ name. DDATA metric chỉ gửi alias, byte và không
        // gì khác; 4294967295 là UInt32 hợp lệ và cũng là -1 hợp lệ, còn birth là chỗ duy nhất từng nói
        // đó là cái nào. Table chỉ nhớ name sẽ đọc trim -1 thành bốn tỷ trong column chấp nhận nó.
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
        // Chỗ duy nhất case Integral không chứa được thứ wire chứa được. Từ chối thay vì wrap thành số
        // âm, vốn sẽ là reading không chỉ sai mà còn nghe có vẻ hợp lý.
        Should.Throw<SparkplugDecodeException>(() =>
            Decode(Named("Formation/Ticks", DataType.Uint64, metric => metric.LongValue = ulong.MaxValue)));
    }

    [Fact]
    public void ADeviceThatSaysItHasNoValueIsRecordedAsSayingSo()
    {
        // Thermocouple bị lỏng gửi is_null. Đọc nó thành 0 °C đưa con số nghe hợp lý vào traceability
        // record; bỏ metric thì nó không khác report-by-exception quyết định không có gì thay đổi. Cả
        // hai đều biến known unknown thành fact.
        var metric = Named("Formation/Temperature", DataType.Float, _ => { });
        metric.IsNull = true;

        Decode(metric).Value.ShouldBe(MetricValue.Absent.Instance);
    }

    [Fact]
    public void AMetricWithNeitherAValueNorIsNullIsRefused()
    {
        // "No value" và "explicitly null" là hai phát biểu khác nhau, còn payload này không đưa ra cái nào.
        Should.Throw<SparkplugDecodeException>(() =>
            Decode(Named("Formation/Voltage", DataType.Float, _ => { })));
    }

    [Fact]
    public void ADeclarationThatContradictsTheWireIsRefused()
    {
        // Birth nói Float còn payload mang text. Chọn bên nào cũng là đoán cái nào sai, và phỏng đoán đó
        // sẽ được ghi thành measurement.
        var thrown = Should.Throw<SparkplugDecodeException>(() =>
            Decode(Named("Formation/Voltage", DataType.Float, metric => metric.StringValue = "3.6875")));

        thrown.Message.ShouldContain("Formation/Voltage");
    }

    [Fact]
    public void ADatatypeThisPipelineDoesNotReadIsRefusedRatherThanSkipped()
    {
        // DataSet, Template, Bytes, File, array. Formation channel không emit cái nào trong số đó, nên
        // một cái đến nghĩa là payload không như pipeline này nghĩ — đáng dừng lại, không đáng âm thầm
        // bỏ một metric.
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
