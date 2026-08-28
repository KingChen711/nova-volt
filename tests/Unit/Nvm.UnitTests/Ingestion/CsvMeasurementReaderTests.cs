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
        // The claim C15 exists to make. A hundred cells were tested and ninety-nine results are fine;
        // rejecting the file would make an operator hand-edit an export to recover them.
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
        // C15.1. Two adapters with two key definitions drift within months, and the symptom is one
        // measurement stored twice for exactly the machines that report through both routes.
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
        // Not a line fault. With no header the columns can only be guessed, and a guess that puts the
        // value in the signal column produces rows that look valid and mean nothing.
        Should.Throw<FileDropFormatException>(() => Reader().Read([Good]));
        Should.Throw<FileDropFormatException>(() => Reader().Read([]));
    }

    [Theory]
    // A local wall-clock time with no offset. Ambiguous for one hour every autumn at DE1, and
    // measured_at is part of the natural key — an ambiguous key merges two different measurements.
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
        // K3, and the same check the MQTT path makes. A row stored under a path that resolves to
        // nothing is a row no query finds and no report misses.
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
        // A trailing newline is not an operator error, and a .error file for one would train people
        // to ignore the rejected directory.
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
