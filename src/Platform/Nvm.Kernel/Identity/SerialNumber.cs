using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Nvm.Kernel.Identity;

/// <summary>
/// The 16-character identifier laser-engraved on a production unit and read back by vision.
/// </summary>
/// <remarks>
/// <para>Layout, per docs/scope.md §6.1:</para>
/// <code>
/// NV1 C L1 6 238 A 00123
/// │   │ │  │ │   │ └──── sequence within the shift, 00001-99999
/// │   │ │  │ │   └────── shift code, A / B / C
/// │   │ │  │ └────────── day of year, 001-366
/// │   │ │  └───────────── last digit of the year, 6 means 2026
/// │   │ └──────────────── line code, for example L1 L2 M1 P1
/// │   └────────────────── unit kind, C cell / M module / P pack
/// └────────────────────── site code, for example NV1 DE1
/// </code>
/// <para>
/// Parsing is deliberately strict: lower case is rejected rather than normalized. The code is
/// engraved in upper case, so a lower-case read means the scanner is misconfigured. Silently
/// upper-casing it would turn a traceable equipment fault into invisible data drift, and this
/// identifier is a legal record (AGENTS.md K4).
/// </para>
/// <para>
/// The year is a single digit, so a serial alone cannot name a calendar date. Resolving
/// <see cref="YearDigit"/> and <see cref="DayOfYear"/> into a real date needs a reference year and
/// belongs to the production calendar, not here. Do not add a DateOnly property that guesses.
/// </para>
/// </remarks>
public sealed record SerialNumber
{
    /// <summary>Number of characters in every serial number.</summary>
    public const int Length = 16;

    private const int SiteStart = 0;
    private const int SiteLength = 3;
    private const int KindIndex = 3;
    private const int LineStart = 4;
    private const int LineLength = 2;
    private const int YearIndex = 6;
    private const int DayStart = 7;
    private const int DayLength = 3;
    private const int ShiftIndex = 10;
    private const int SequenceStart = 11;
    private const int SequenceLength = 5;

    private SerialNumber(
        string value,
        string siteCode,
        ProductionUnitKind kind,
        string lineCode,
        int yearDigit,
        int dayOfYear,
        char shiftCode,
        int sequence)
    {
        Value = value;
        SiteCode = siteCode;
        Kind = kind;
        LineCode = lineCode;
        YearDigit = yearDigit;
        DayOfYear = dayOfYear;
        ShiftCode = shiftCode;
        Sequence = sequence;
    }

    /// <summary>The raw 16-character code, exactly as engraved.</summary>
    public string Value { get; }

    /// <summary>Three-character site code, for example NV1.</summary>
    public string SiteCode { get; }

    /// <summary>Which tier of unit this serial identifies.</summary>
    public ProductionUnitKind Kind { get; }

    /// <summary>Two-character line code, for example L1.</summary>
    public string LineCode { get; }

    /// <summary>Last digit of the production year. Needs a reference year to become a date.</summary>
    public int YearDigit { get; }

    /// <summary>Day of year, 1 to 366.</summary>
    public int DayOfYear { get; }

    /// <summary>Shift code, A, B or C.</summary>
    public char ShiftCode { get; }

    /// <summary>Sequence within the shift, 1 to 99999.</summary>
    public int Sequence { get; }

    /// <summary>Parses a serial number, throwing when the code is malformed.</summary>
    /// <exception cref="FormatException">The code does not match the engraved layout.</exception>
    public static SerialNumber Parse(string? value) =>
        TryParse(value, out var serial)
            ? serial
            : throw new FormatException("Not a valid 16-character serial number: '" + value + "'.");

    /// <summary>Parses a serial number, returning false when the code is malformed.</summary>
    public static bool TryParse(
        [NotNullWhen(true)] string? value,
        [NotNullWhen(true)] out SerialNumber? serial)
    {
        serial = null;

        if (value is null || value.Length != Length)
        {
            return false;
        }

        var span = value.AsSpan();

        if (!IsUpperAlphanumeric(span))
        {
            return false;
        }

        if (!TryReadKind(span[KindIndex], out var kind))
        {
            return false;
        }

        // Line is a letter followed by a digit, which keeps L1 valid and 11 invalid.
        if (!char.IsAsciiLetterUpper(span[LineStart]) || !char.IsAsciiDigit(span[LineStart + 1]))
        {
            return false;
        }

        if (!char.IsAsciiDigit(span[YearIndex]))
        {
            return false;
        }

        if (!TryReadNumber(span.Slice(DayStart, DayLength), 1, 366, out var dayOfYear))
        {
            return false;
        }

        var shiftCode = span[ShiftIndex];
        if (shiftCode is not ('A' or 'B' or 'C'))
        {
            return false;
        }

        if (!TryReadNumber(span.Slice(SequenceStart, SequenceLength), 1, 99_999, out var sequence))
        {
            return false;
        }

        serial = new SerialNumber(
            value,
            value.Substring(SiteStart, SiteLength),
            kind,
            value.Substring(LineStart, LineLength),
            span[YearIndex] - '0',
            dayOfYear,
            shiftCode,
            sequence);

        return true;
    }

    /// <summary>Returns the raw engraved code.</summary>
    public override string ToString() => Value;

    private static bool IsUpperAlphanumeric(ReadOnlySpan<char> span)
    {
        foreach (var character in span)
        {
            if (!char.IsAsciiDigit(character) && !char.IsAsciiLetterUpper(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadKind(char code, out ProductionUnitKind kind)
    {
        switch (code)
        {
            case 'C':
                kind = ProductionUnitKind.Cell;
                return true;
            case 'M':
                kind = ProductionUnitKind.Module;
                return true;
            case 'P':
                kind = ProductionUnitKind.Pack;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryReadNumber(ReadOnlySpan<char> span, int minimum, int maximum, out int number)
    {
        // NumberStyles.None rejects signs, decimal points and whitespace, so only digits parse.
        return int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out number)
               && number >= minimum
               && number <= maximum;
    }
}
