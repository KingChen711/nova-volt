using Org.Eclipse.Tahu.Protobuf;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Decode các bytes Sparkplug B thật bằng code sinh ra từ schema đã vendor.</summary>
/// <remarks>
/// <para>
/// Các payload được tạo ra bởi <c>pysparkplug</c>, thứ mang theo bản copy schema Sparkplug của riêng
/// nó — xem <c>tests/Fixtures/sparkplug/README.md</c>. Đó chính là toàn bộ mục đích của chúng. Encode
/// bằng code tự sinh của chúng ta rồi decode lại nó cũng sẽ pass vui vẻ y hệt khi cả hai chiều đều
/// sai, và một schema chính xác là loại thứ có thể sai ở cả hai chiều cùng một lúc.
/// </para>
/// <para>
/// Cặp này là một DBIRTH và DDATA theo sau nó, vì trên một line thật thì không cái nào có ý nghĩa gì
/// khi đứng một mình. Mối quan hệ đó là thứ C11 biến thành node state; ở đây nó chỉ được decode mà
/// thôi.
/// </para>
/// </remarks>
public sealed class SparkplugPayloadTests
{
    /// <summary>2026-08-28T07:15:30.500Z. Timestamp Sparkplug là số mili-giây kể từ Unix epoch, UTC.</summary>
    private const ulong BirthTimestampMs = 1787901330500;

    /// <summary>Hai giây sau đó — bản update report-by-exception.</summary>
    private const ulong DataTimestampMs = 1787901332500;

    [Fact]
    public void ADeviceBirthDeclaresEveryMetricByNameAliasAndType()
    {
        var birth = Payload.Parser.ParseFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        birth.Timestamp.ShouldBe(BirthTimestampMs);
        birth.Seq.ShouldBe(1UL);
        birth.Metrics.Count.ShouldBe(5);

        birth.Metrics.Select(metric => metric.Name).ShouldBe(
        [
            "Formation/Voltage",
            "Formation/Current",
            "Formation/Temperature",
            "Formation/StepIndex",
            "Formation/CellSerial",
        ]);

        // Alias chỉ được gán ở đây và không nơi nào khác. Mọi thứ sau message này tham chiếu tới một
        // measurement bằng con số của nó.
        birth.Metrics.Select(metric => metric.Alias).ShouldBe([1UL, 2UL, 3UL, 4UL, 5UL]);

        // So sánh bằng chính xác, không phải dung sai: fixture dùng các giá trị mà binary32 biểu diễn
        // chính xác, nên một decoder đọc bốn byte từ sai offset sẽ rơi vào một giá trị rõ ràng khác
        // hẳn thay vì một giá trị đủ gần để pass.
        var voltage = birth.Metrics[0];
        voltage.Datatype.ShouldBe((uint)DataType.Float);
        voltage.ValueCase.ShouldBe(Payload.Types.Metric.ValueOneofCase.FloatValue);
        voltage.FloatValue.ShouldBe(3.6875f);

        // Ba datatype trong một payload, nên oneof thực sự được kiểm thử. Một decoder luôn đọc
        // float_value sẽ pass một payload chỉ toàn float.
        birth.Metrics[3].Datatype.ShouldBe((uint)DataType.Int32);
        birth.Metrics[3].IntValue.ShouldBe(2U);

        birth.Metrics[4].Datatype.ShouldBe((uint)DataType.String);
        birth.Metrics[4].StringValue.ShouldBe("NV1CL16238A00123");
    }

    [Fact]
    public void AReportByExceptionUpdateCarriesAliasesAndNothingElse()
    {
        var data = Payload.Parser.ParseFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        data.Timestamp.ShouldBe(DataTimestampMs);
        data.Seq.ShouldBe(2UL);

        // Hai trong số năm, vì chỉ có hai giá trị thay đổi. "Không gì tới" trên thiết bị này do đó
        // vừa có thể nghĩa là "không gì thay đổi" vừa có thể nghĩa là "đường truyền đang gián đoạn",
        // và chỉ NDEATH mới phân biệt được hai trường hợp đó — xem mục report-by-exception trong
        // docs/glossary.md.
        data.Metrics.Count.ShouldBe(2);

        foreach (var metric in data.Metrics)
        {
            metric.HasName.ShouldBeFalse("a DDATA carries no names — that is what the aliases are for");
            metric.HasDatatype.ShouldBeFalse("the datatype was declared at birth and is not repeated");
            metric.HasAlias.ShouldBeTrue();
        }

        data.Metrics.Select(metric => metric.Alias).ShouldBe([1UL, 3UL]);
        data.Metrics.Select(metric => metric.FloatValue).ShouldBe([3.71875f, 31.75f]);
    }

    [Fact]
    public void OnlyTheBirthMakesTheUpdateReadable()
    {
        // Phép join mà C11 sẽ phải giữ trong bộ nhớ cho mỗi edge node, được làm thủ công ở đây để cái
        // giá của việc mất nó trở nên rõ ràng: thiếu birth, alias 1 chỉ là một con số với một float
        // gắn theo nó, và dù retry DDATA bao nhiêu lần cũng không khôi phục lại được nó đã đo gì.
        var birth = Payload.Parser.ParseFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));
        var data = Payload.Parser.ParseFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        var names = birth.Metrics.ToDictionary(metric => metric.Alias, metric => metric.Name);

        var readings = data.Metrics.ToDictionary(metric => names[metric.Alias], metric => metric.FloatValue);

        readings.ShouldBe(new Dictionary<string, float>
        {
            ["Formation/Voltage"] = 3.71875f,
            ["Formation/Temperature"] = 31.75f,
        });
    }
}
