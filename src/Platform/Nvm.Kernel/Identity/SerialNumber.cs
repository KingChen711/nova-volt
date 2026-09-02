using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Nvm.Kernel.Identity;

/// <summary>
/// Định danh 16 ký tự được khắc laser lên một production unit và được vision đọc lại.
/// </summary>
/// <remarks>
/// <para>Layout, theo docs/scope.md §6.1:</para>
/// <code>
/// NV1 C L1 6 238 A 00123
/// │   │ │  │ │   │ └──── số thứ tự trong ca, 00001-99999
/// │   │ │  │ │   └────── shift code, A / B / C
/// │   │ │  │ └────────── ngày thứ mấy trong năm, 001-366
/// │   │ │  └───────────── chữ số cuối của năm, 6 nghĩa là 2026
/// │   │ └──────────────── line code, ví dụ L1 L2 M1 P1
/// │   └────────────────── loại unit, C cell / M module / P pack
/// └────────────────────── site code, ví dụ NV1 DE1
/// </code>
/// <para>
/// Việc parse cố tình nghiêm ngặt: chữ thường bị từ chối thay vì được chuẩn hóa. Code được khắc bằng
/// chữ hoa, nên đọc được chữ thường nghĩa là scanner đang cấu hình sai. Âm thầm chuyển nó thành chữ
/// hoa sẽ biến một lỗi thiết bị có thể truy vết thành một sai lệch dữ liệu vô hình, và định danh này
/// là một bản ghi mang tính pháp lý (AGENTS.md K4).
/// </para>
/// <para>
/// Năm chỉ là một chữ số, nên riêng một serial không thể nêu tên một ngày trên lịch. Việc phân giải
/// <see cref="YearDigit"/> và <see cref="DayOfYear"/> thành một ngày thực cần một năm tham chiếu và
/// thuộc về production calendar, không thuộc về đây. Không thêm một property DateOnly đi đoán bừa.
/// </para>
/// </remarks>
public sealed record SerialNumber
{
    /// <summary>Số ký tự trong mỗi serial number.</summary>
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

    /// <summary>Code thô 16 ký tự, đúng như khi được khắc.</summary>
    public string Value { get; }

    /// <summary>Site code ba ký tự, ví dụ NV1.</summary>
    public string SiteCode { get; }

    /// <summary>Serial này định danh tier unit nào.</summary>
    public ProductionUnitKind Kind { get; }

    /// <summary>Line code hai ký tự, ví dụ L1.</summary>
    public string LineCode { get; }

    /// <summary>Chữ số cuối của năm sản xuất. Cần một năm tham chiếu để trở thành một ngày.</summary>
    public int YearDigit { get; }

    /// <summary>Ngày thứ mấy trong năm, 1 đến 366.</summary>
    public int DayOfYear { get; }

    /// <summary>Shift code, A, B hoặc C.</summary>
    public char ShiftCode { get; }

    /// <summary>Số thứ tự trong ca, 1 đến 99999.</summary>
    public int Sequence { get; }

    /// <summary>Parse một serial number, ném lỗi khi code sai định dạng.</summary>
    /// <exception cref="FormatException">Code không khớp layout đã khắc.</exception>
    public static SerialNumber Parse(string? value) =>
        TryParse(value, out var serial)
            ? serial
            : throw new FormatException("Not a valid 16-character serial number: '" + value + "'.");

    /// <summary>Parse một serial number, trả về false khi code sai định dạng.</summary>
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

        // Line là một chữ cái theo sau bởi một chữ số, điều này giữ L1 hợp lệ và 11 không hợp lệ.
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

    /// <summary>Trả về code thô đã khắc.</summary>
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
        // NumberStyles.None từ chối dấu, dấu chấm thập phân và khoảng trắng, nên chỉ chữ số mới parse được.
        return int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out number)
               && number >= minimum
               && number <= maximum;
    }
}
