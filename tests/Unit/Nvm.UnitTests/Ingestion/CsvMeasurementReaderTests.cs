using Nvm.Ingestion.FileDrop;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.Ingestion;

public sealed class CsvMeasurementReaderTests
{
    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

    private const string Good =
        "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/Capacity,2026-08-28T09:28:11.004Z,real,4.812";

    [Fact]
    public void OneBadLineAmongMany_DoesNotCostTheGoodOnes()
    {
        // Đây là khẳng định mà C15 tồn tại để đưa ra. Một trăm cell đã được test, chín mươi chín kết quả
        // ổn; từ chối file sẽ bắt operator sửa tay export để khôi phục chúng.
        var lines = new List<string> { CsvMeasurementReader.Header };
        lines.AddRange(Enumerable.Range(0, 99).Select(index => Good.Replace(
            "2026-08-28T09:28:11.004Z",
            $"2026-08-28T09:28:11.{index:000}Z",
            StringComparison.Ordinal)));
        lines.Insert(50, Good.Replace(",real,4.812", ",real,not-a-number", StringComparison.Ordinal));

        var result = Reader().Read(lines);

        result.Measurements.Count.ShouldBe(99);
        var rejected = result.Rejected.ShouldHaveSingleItem();
        rejected.LineNumber.ShouldBe(51);
        rejected.Reason.ShouldContain("not-a-number");
        rejected.Line.ShouldContain("not-a-number");
    }

    [Fact]
    public void AParsedLine_BuildsTheSameNaturalKeyAsTheMqttPath()
    {
        // C15.1. Hai adapter với hai định nghĩa key sẽ drift trong vài tháng, và triệu chứng là một
        // measurement được lưu hai lần đúng ở các máy report qua cả hai route.
        var measurement = Reader().Read([CsvMeasurementReader.Header, Good]).Measurements.ShouldHaveSingleItem();

        var fromFile = measurement.Reading.NaturalKey(measurement.EquipmentPath, measurement.UnitId);
        var fromDevice = new DeviceReading(
            "Formation/Capacity",
            Alias: 4,
            new MetricValue.Real(4.812),
            new DateTimeOffset(2026, 8, 28, 9, 28, 11, 4, TimeSpan.Zero)).NaturalKey(Channel);

        fromFile.SourceEventId.ShouldBe(fromDevice.SourceEventId);
    }

    [Fact]
    public void MissingOrWrongHeader_RejectsTheWholeFile()
    {
        // Không phải line fault. Không có header thì chỉ có thể đoán cột, và đoán đưa value vào signal
        // column sẽ tạo các row trông hợp lệ nhưng vô nghĩa.
        Should.Throw<FileDropFormatException>(() => Reader().Read([Good]));
        Should.Throw<FileDropFormatException>(() => Reader().Read([]));
    }

    [Theory]
    // Local wall-clock time không có offset. Nó ambiguous trong một giờ mỗi mùa thu ở DE1, và
    // measured_at là một phần natural key — key ambiguous sẽ gộp hai measurement khác nhau.
    [InlineData("2026-08-28T09:28:11.004", "offset")]
    [InlineData("not-a-time", "offset")]
    [InlineData("", "offset")]
    public void ATimestampWithoutAnExplicitOffset_IsRefused(string timestamp, string expectedReason)
    {
        var line = Good.Replace("2026-08-28T09:28:11.004Z", timestamp, StringComparison.Ordinal);

        var rejected = Reader().Read([CsvMeasurementReader.Header, line]).Rejected.ShouldHaveSingleItem();

        rejected.Reason.ShouldContain(expectedReason);
    }

    [Fact]
    public void AnEquipmentPathOutsideTheActiveModel_IsRefusedAtTheServer()
    {
        // K3, cũng là check mà MQTT path thực hiện. Row lưu dưới path resolve thành nothing sẽ là row
        // không query nào tìm được và không report nào bỏ sót.
        var line = Good.Replace("FORM-01-CH-0142", "FORM-99-CH-9999", StringComparison.Ordinal);

        var rejected = Reader().Read([CsvMeasurementReader.Header, line]).Rejected.ShouldHaveSingleItem();

        rejected.Reason.ShouldContain("active model");
    }

    [Fact]
    public void WrongColumnCount_NamesTheHeaderInTheReason()
    {
        var rejected = Reader()
            .Read([CsvMeasurementReader.Header, "NOVAVOLT/NV1/FORMATION/F1,Formation/Capacity"])
            .Rejected
            .ShouldHaveSingleItem();

        rejected.Reason.ShouldContain(CsvMeasurementReader.Header);
    }

    [Fact]
    public void BlankLines_AreSkippedRatherThanRejected()
    {
        // Trailing newline không phải operator error, và file .error chỉ vì nó sẽ khiến mọi người học
        // cách phớt lờ rejected directory.
        var result = Reader().Read([CsvMeasurementReader.Header, Good, string.Empty, "   "]);

        result.Measurements.Count.ShouldBe(1);
        result.Rejected.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("integer,42")]
    [InlineData("boolean,true")]
    [InlineData("text,NV1C0142-CELL-00042")]
    public void EveryValueKind_Parses(string kindAndValue)
    {
        var line = Good.Replace("real,4.812", kindAndValue, StringComparison.Ordinal);

        Reader().Read([CsvMeasurementReader.Header, line]).Rejected.ShouldBeEmpty();
    }

    [Fact]
    public void AnUnknownValueKind_IsRefused()
    {
        var line = Good.Replace("real,4.812", "decimal,4.812", StringComparison.Ordinal);

        Reader().Read([CsvMeasurementReader.Header, line])
            .Rejected.ShouldHaveSingleItem()
            .Reason.ShouldContain("value kind");
    }

    private static CsvMeasurementReader Reader() => new(new OneChannelDirectory());

    private sealed class OneChannelDirectory : IEquipmentDirectory
    {
        public bool Contains(EquipmentPath path) => path == Line || path == Channel;

        public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode) =>
            line == Line && string.Equals(deviceCode, Channel.Code, StringComparison.Ordinal)
                ? Channel
                : null;
    }
}
