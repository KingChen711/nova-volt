using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

public sealed class SerialNumberTests
{
    // Ví dụ đã tính trong docs/scope.md §6.1.
    private const string ValidCellSerial = "NV1CL16238A00123";

    [Fact]
    public void Parse_ValidCellSerial_ExposesEveryComponent()
    {
        var serial = SerialNumber.Parse(ValidCellSerial);

        serial.SiteCode.ShouldBe("NV1");
        serial.Kind.ShouldBe(ProductionUnitKind.Cell);
        serial.LineCode.ShouldBe("L1");
        serial.YearDigit.ShouldBe(6);
        serial.DayOfYear.ShouldBe(238);
        serial.ShiftCode.ShouldBe('A');
        serial.Sequence.ShouldBe(123);
    }

    [Theory]
    [InlineData("NV1CL16238A00123", ProductionUnitKind.Cell)]
    [InlineData("NV1MM16238B00007", ProductionUnitKind.Module)]
    [InlineData("DE1PP16238C99999", ProductionUnitKind.Pack)]
    public void Parse_KnownKindCharacter_MapsToUnitKind(string code, ProductionUnitKind expected)
    {
        SerialNumber.Parse(code).Kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("NV1CL16238A0012", "15 characters, one short")]
    [InlineData("NV1CL16238A001234", "17 characters, one long")]
    [InlineData("NV1XL16238A00123", "unit kind X is not C, M or P")]
    [InlineData("NV1C1116238A0012", "line 11 has no leading letter")]
    [InlineData("NV1CL1X238A00123", "year digit is not a digit")]
    [InlineData("NV1CL16000A00123", "day of year 000 is below 001")]
    [InlineData("NV1CL16367A00123", "day of year 367 is above 366")]
    [InlineData("NV1CL16238D00123", "shift D is not A, B or C")]
    [InlineData("NV1CL16238A00000", "sequence 00000 is below 00001")]
    [InlineData("NV1CL16238A0012X", "sequence contains a letter")]
    [InlineData("NV1CL16238A-0123", "hyphen is neither digit nor upper-case letter")]
    public void TryParse_MalformedCode_ReturnsFalse(string? code, string reason)
    {
        var parsed = SerialNumber.TryParse(code, out var serial);

        parsed.ShouldBeFalse(reason);
        serial.ShouldBeNull(reason);
    }

    [Fact]
    public void TryParse_LowerCaseCode_IsRejectedRatherThanNormalized()
    {
        // Có chủ ý: code được khắc bằng chữ hoa, nên đọc ra chữ thường nghĩa là scanner bị
        // cấu hình sai. Normalize sẽ che giấu equipment fault.
        SerialNumber.TryParse(ValidCellSerial.ToLowerInvariant(), out _).ShouldBeFalse();
    }

    [Fact]
    public void Parse_MalformedCode_ThrowsFormatExceptionNamingTheInput()
    {
        var exception = Should.Throw<FormatException>(() => SerialNumber.Parse("NOPE"));

        // Operator cần thấy thứ đã scan thực sự, không chỉ "invalid input".
        exception.Message.ShouldContain("NOPE");
    }

    [Fact]
    public void ToString_ReturnsTheEngravedCodeUnchanged()
    {
        SerialNumber.Parse(ValidCellSerial).ToString().ShouldBe(ValidCellSerial);
    }

    [Fact]
    public void Equality_TwoParsesOfTheSameCode_AreEqual()
    {
        var first = SerialNumber.Parse(ValidCellSerial);
        var second = SerialNumber.Parse(ValidCellSerial);

        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void Parse_DoesNotExposeACalendarDate()
    {
        // Một chữ số năm không gọi tên được một năm. Resolve nó cần reference year và thuộc về
        // production calendar (M3), nên ở đây không được có property DateOnly.
        typeof(SerialNumber)
            .GetProperties()
            .ShouldNotContain(property => property.PropertyType == typeof(DateOnly));
    }
}
