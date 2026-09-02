using Nvm.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Những gì một birth bắt buộc phải nói, và điều gì xảy ra khi nó không nói ra.</summary>
/// <remarks>
/// Nghiêm ngặt một cách có chủ đích. Một birth là lời khai báo duy nhất về ý nghĩa của mọi message
/// sau đó trong session, và nó chỉ tới một lần; một birth được chấp nhận dù có một lỗ hổng sẽ biến
/// thành hàng giờ reading không thể diễn giải được, chỉ phát hiện ra rất lâu sau khi node đã đi tiếp.
/// </remarks>
public sealed class SparkplugBirthTests
{
    [Fact]
    public void ABirthMetricWithoutANameIsRefused()
    {
        // Một metric chỉ mang alias là bình thường trong một DDATA và vô nghĩa trong một birth: birth
        // chính là nơi con số đó phải có được ý nghĩa.
        var metric = new SparkplugMetric
        {
            Alias = 1,
            Datatype = (uint)DataType.Float,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        Should.Throw<SparkplugDecodeException>(() =>
            SparkplugPayload.DecodeBirth(SparkplugPayloads.Carrying(metric)));
    }

    [Fact]
    public void ABirthMetricWithoutADatatypeIsRefused()
    {
        // Giá trị có thể suy ra được từ trường nó tới trong đó, và với một float thì suy đoán đó thậm
        // chí đúng. Nó vẫn bị từ chối vì các số nguyên: int_value mang cả Int32 lẫn UInt32, nên một
        // birth không khai báo sẽ khiến -1 và 4294967295 không thể phân biệt được cho mọi message
        // trong suốt phần còn lại của session.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Alias = 1,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        var thrown = Should.Throw<SparkplugDecodeException>(() =>
            SparkplugPayload.DecodeBirth(SparkplugPayloads.Carrying(metric)));

        thrown.Message.ShouldContain("Formation/Voltage");
    }

    [Fact]
    public void ABirthThatGivesOneAliasToTwoMetricsIsRefused()
    {
        // Bất kể cái nào trong hai cái được đọc sau cùng sẽ thắng, và mọi message sau đó dùng alias
        // này sẽ bị gán vào nó. Từ chối birth tốn một lần rebirth; chấp nhận nó tốn cả một session
        // reading bị gán nhầm cho sai tín hiệu.
        var payload = SparkplugPayloads.BirthDeclaring(
            ("Formation/Voltage", 1),
            ("Formation/Temperature", 1));

        var thrown = Should.Throw<SparkplugDecodeException>(() => SparkplugPayload.DecodeBirth(payload));

        thrown.Message.ShouldContain("Formation/Voltage");
        thrown.Message.ShouldContain("Formation/Temperature");
    }

    [Fact]
    public void ABirthThatDeclaresOneMetricTwiceIsRefused()
    {
        var payload = SparkplugPayloads.BirthDeclaring(
            ("Formation/Voltage", 1),
            ("Formation/Voltage", 2));

        Should.Throw<SparkplugDecodeException>(() => SparkplugPayload.DecodeBirth(payload));
    }

    [Fact]
    public void AMetricWithNeitherANameNorAnAliasIsRefused()
    {
        // Không có gì để gán giá trị này vào. Sparkplug cho phép nó xuất hiện trên wire; nhưng không
        // có cách nào đọc được nó.
        var metric = new SparkplugMetric
        {
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        Should.Throw<SparkplugDecodeException>(() =>
            SparkplugPayload.DecodeData(SparkplugPayloads.Carrying(metric), MetricAliasTable.Empty));
    }

    [Fact]
    public void ANamedMetricIsAcceptedMidSessionWithoutAnAliasTable()
    {
        // Nửa còn lại của quy tắc alias, và là nửa khiến nó không trở thành một sự từ chối trên diện
        // rộng: một metric tự đặt tên mình không cần bảng nào cả, vì nó không yêu cầu ai phải nhớ gì
        // hết.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        var readings = SparkplugPayload.DecodeData(
            SparkplugPayloads.Carrying(metric),
            MetricAliasTable.Empty);

        readings.Single().MetricName.ShouldBe("Formation/Voltage");
        readings.Single().Alias.ShouldBeNull();
    }

    [Fact]
    public void AnAliasThatArrivesUnderADifferentNameThanTheBirthGaveItIsRefused()
    {
        // Reading này lẽ ra có thể được gán đúng — vì nó tự đặt tên mình. Message kế tiếp dùng alias 1
        // thì không thể: nó sẽ không mang tên, và bảng cũ sẽ gửi nó tới Formation/Voltage. Chấp nhận
        // mâu thuẫn này ở đây mua được một reading đúng và phải trả giá bằng mọi message chỉ-mang-alias
        // theo sau.
        var birth = SparkplugPayload.DecodeBirth(SparkplugPayloads.BirthDeclaring(("Formation/Voltage", 1)));

        var renamed = new SparkplugMetric
        {
            Name = "Formation/Temperature",
            Alias = 1,
            Datatype = (uint)DataType.Float,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 31.5f,
        };

        var thrown = Should.Throw<UnknownMetricAliasException>(() =>
            SparkplugPayload.DecodeData(SparkplugPayloads.Carrying(renamed), birth.Aliases));

        thrown.Message.ShouldContain("Formation/Voltage");
        thrown.Message.ShouldContain("Formation/Temperature");
    }

    [Fact]
    public void ABirthWithNoAliasesAtAllProducesAnEmptyTableRatherThanFailing()
    {
        // Alias là một tối ưu hóa, không phải một yêu cầu bắt buộc. Một thiết bị viết đầy đủ mọi tên
        // ra là lãng phí nhưng hoàn toàn hợp lệ, và birth của nó vẫn phải decode được.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        var birth = SparkplugPayload.DecodeBirth(SparkplugPayloads.Carrying(metric));

        birth.Readings.Length.ShouldBe(1);
        birth.Readings[0].Alias.ShouldBeNull();
        birth.Aliases.Count.ShouldBe(0);
        birth.Aliases.ShouldBeSameAs(MetricAliasTable.Empty);
    }
}
