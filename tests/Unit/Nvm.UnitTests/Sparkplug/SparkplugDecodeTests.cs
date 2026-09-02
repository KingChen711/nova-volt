using Nvm.Sparkplug;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Decode các payload thật đã ghi lại thành các reading.</summary>
/// <remarks>
/// Các bytes tới từ <c>tests/Fixtures/sparkplug/</c> và được tạo ra bởi một implementation Sparkplug
/// khác, nên các test này kiểm tra decoder dựa trên specification chứ không phải dựa trên chính nó.
/// <c>SparkplugValueTests</c> thay vào đó tự dựng payload trong process, điều này ổn cho câu hỏi nó
/// đặt ra — datatype nào map vào case nào — vì schema đã được pin sẵn ở đây và bởi
/// <c>SparkplugPinTests</c>.
/// </remarks>
public sealed class SparkplugDecodeTests
{
    /// <summary>2026-08-28T07:15:30.500Z, thời điểm mà fixture birth được đóng dấu.</summary>
    private static readonly DateTimeOffset BirthInstant =
        DateTimeOffset.FromUnixTimeMilliseconds(1787901330500);

    /// <summary>Hai giây sau đó — bản update report-by-exception.</summary>
    private static readonly DateTimeOffset DataInstant =
        DateTimeOffset.FromUnixTimeMilliseconds(1787901332500);

    [Fact]
    public void ABirthDeclaresEveryMetricAndBuildsTheAliasTable()
    {
        var birth = SparkplugPayload.DecodeBirth(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        birth.Readings.Select(reading => reading.MetricName).ShouldBe(
        [
            "Formation/Voltage",
            "Formation/Current",
            "Formation/Temperature",
            "Formation/StepIndex",
            "Formation/CellSerial",
        ]);

        birth.Readings.Select(reading => reading.Alias).ShouldBe([1UL, 2UL, 3UL, 4UL, 5UL]);

        // Birth báo cáo cả giá trị hiện tại lẫn khai báo tên. Đó chính là điều cho phép một listener
        // vừa mới kết nối hiển thị một bức tranh đầy đủ thay vì một bức tranh trống rỗng cho tới khi
        // mọi metric tình cờ thay đổi.
        birth.Readings[0].Value.ShouldBe(new MetricValue.Real(3.6875));
        birth.Readings[3].Value.ShouldBe(new MetricValue.Integral(2));
        birth.Readings[4].Value.ShouldBe(new MetricValue.Text("NV1CL16238A00123"));

        birth.Aliases.Count.ShouldBe(5);
        birth.Aliases.Aliases.ShouldBe([1UL, 2UL, 3UL, 4UL, 5UL]);
        birth.Aliases.TryGetMetricName(3, out var name).ShouldBeTrue();
        name.ShouldBe("Formation/Temperature");
    }

    [Fact]
    public void AnUpdateResolvesItsAliasesThroughTheBirth()
    {
        var birth = SparkplugPayload.DecodeBirth(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        var readings = SparkplugPayload.DecodeData(
            SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData),
            birth.Aliases);

        // Hai trong số năm, vì chỉ có hai giá trị thay đổi. Bản thân payload không mang tên cũng
        // không mang datatype cho chúng — mọi thứ bên dưới được khôi phục từ birth.
        readings.Length.ShouldBe(2);

        readings.Select(reading => reading.MetricName)
            .ShouldBe(["Formation/Voltage", "Formation/Temperature"]);

        readings.Select(reading => reading.Value)
            .ShouldBe([new MetricValue.Real(3.71875), new MetricValue.Real(31.75)]);
    }

    [Fact]
    public void TheSameUpdateWithoutABirthThrowsRatherThanReturningNothing()
    {
        // Thất bại mà test này tồn tại vì nó không phải là exception; đó là phương án thay thế. Một
        // decoder bỏ qua các alias nó không resolve được sẽ trả về một mảng rỗng ở đây, và một mảng
        // rỗng chính xác là những gì report-by-exception tạo ra khi không có gì thay đổi. Ingestion
        // sẽ ghi nhận một channel khỏe mạnh mà không có reading nào, và khoảng trống đó sẽ trông như
        // một cỗ máy đang im lặng.
        var thrown = Should.Throw<UnknownMetricAliasException>(() => SparkplugPayload.DecodeData(
            SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData),
            MetricAliasTable.Empty));

        thrown.Alias.ShouldBe(1UL);
        thrown.KnownAliasCount.ShouldBe(0);
    }

    [Fact]
    public void AnAliasTableFromTheWrongSessionIsNotSilentlyAcceptedEither()
    {
        // Một node kết nối lại có thể đánh số lại tự do. Ở đây bảng biết alias 1 nhưng không biết
        // alias 3, đó chính là hình dạng của một bảng cũ một phần — và nửa nó resolve được mới là
        // nửa nguy hiểm, vì nó khiến kết quả trông có vẻ hợp lý.
        var partial = SparkplugPayload
            .DecodeBirth(SparkplugPayloads.BirthDeclaring(("Formation/Voltage", 1)))
            .Aliases;

        var thrown = Should.Throw<UnknownMetricAliasException>(() => SparkplugPayload.DecodeData(
            SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData),
            partial));

        thrown.Alias.ShouldBe(3UL);
        thrown.KnownAliasCount.ShouldBe(1);
    }

    [Fact]
    public void EveryReadingCarriesTheDeviceClock()
    {
        var birth = SparkplugPayload.DecodeBirth(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        var readings = SparkplugPayload.DecodeData(
            SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData),
            birth.Aliases);

        birth.Readings.ShouldAllBe(reading => reading.DeviceTimestamp == BirthInstant);
        readings.ShouldAllBe(reading => reading.DeviceTimestamp == DataInstant);

        // Không phải thời điểm decode, và không phải đồng hồ của gateway. docs/scope.md §7.3 giữ ba
        // cái này tách biệt vì đồng hồ của thiết bị là cái thường xuyên sai lệch hàng giờ, và C13
        // phải có khả năng nói ra điều đó thay vì bị âm thầm thay thế ở đây.
        readings[0].DeviceTimestamp.ShouldBe(DataInstant);
        readings[0].DeviceTimestamp.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ATruncatedPayloadIsRefused()
    {
        // Không phải giả định suông: C09 buffer các payload trên đĩa, và một bản ghi bị cắt cụt bởi
        // sự cố mất điện chính là loại lỗi mà buffer đó được thiết kế để đối phó. Nó phải tới đây
        // dưới dạng một lỗi decode chứ không phải một payload có ít metric hơn số đã được ghi.
        var complete = SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth);
        var truncated = complete[..^5];

        Should.Throw<SparkplugDecodeException>(() => SparkplugPayload.DecodeBirth(truncated));
    }
}
