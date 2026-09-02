using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Nvm.Kernel.Identity;

/// <summary>
/// Một vị trí trong phân cấp ISA-95, được viết dưới dạng
/// <c>NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142</c>.
/// </summary>
/// <remarks>
/// <para>
/// Chuỗi được tái sử dụng nhiều nhất trong toàn hệ thống (docs/scope.md §2.1): nó là MQTT topic, nhãn
/// metric, khoá phân quyền, ràng buộc XPath trong Mendix, và field <c>equipmentId</c> của một event.
/// Quyết định một lần, dùng khắp mọi nơi — nghĩa là một sai sót ở đây cũng là sai sót ở sáu chỗ khác.
/// </para>
/// <para>
/// Một path có thể dừng ở bất kỳ cấp nào. Một area có một path hoàn toàn hợp lệ, và dashboard của một
/// supervisor hỏi câu hỏi ở cấp đó trong khi một recall hỏi ở cấp equipment. Cả hai đều dùng type này,
/// và cấp được đọc ra từ số lượng segment thay vì được lưu riêng.
/// </para>
/// <para>
/// So sánh có phân biệt hoa thường, và chữ thường bị từ chối thay vì được chuẩn hoá. Mã được khắc trên
/// máy và in trên phiếu routing bằng chữ hoa; một lần đọc ra chữ thường nghĩa là có gì đó ở tầng trên
/// bị cấu hình sai. Âm thầm chuyển thành chữ hoa sẽ khiến một máy có hai node trong cây và chia đôi
/// lịch sử của nó.
/// </para>
/// <para>
/// Cái tên là của nhà máy, không phải do đặt ra: <c>equipment_path</c> chính là thuật ngữ được dùng
/// trên sàn sản xuất, kể cả cho một path dừng ở cấp area.
/// </para>
/// </remarks>
public sealed record EquipmentPath
{
    /// <summary>Ký tự phân tách giữa các segment.</summary>
    public const char Separator = '/';

    private const int MinSegments = (int)FactoryNodeKind.Enterprise;
    private const int MaxSegments = (int)FactoryNodeKind.Equipment;
    private const int SiteSegmentIndex = (int)FactoryNodeKind.Site - 1;

    private readonly ImmutableArray<string> _segments;

    private EquipmentPath(string value, ImmutableArray<string> segments)
    {
        Value = value;
        _segments = segments;
    }

    /// <summary>Toàn bộ path, đúng như nó được viết ở mọi nơi khác.</summary>
    public string Value { get; }

    /// <summary>Path này đặt tên cho cấp nào trong phân cấp, lấy ra từ độ sâu của nó.</summary>
    public FactoryNodeKind Kind => (FactoryNodeKind)_segments.Length;

    /// <summary>Các segment, ngoài cùng trước.</summary>
    /// <remarks>
    /// Dùng <see cref="ImmutableArray{T}"/> thay vì <c>IReadOnlyList</c>, và khác biệt này không phải
    /// chuyện phong cách. <c>IReadOnlyList</c> chỉ đảm bảo rằng <i>chính reference này</i> không cung
    /// cấp mutator; một caller vẫn có thể cast ngược nó về mảng bên dưới và ghi thẳng qua đó. Làm vậy ở
    /// đây sẽ khiến <see cref="Value"/> nói một đằng còn <see cref="SiteId"/>, <see cref="Code"/> và
    /// <see cref="Kind"/> nói một nẻo — một path đặt tên cho một máy nhưng lại tự báo cáo mình là một
    /// máy khác, trong đúng chuỗi mà cả hệ thống dùng làm khoá.
    /// </remarks>
    public ImmutableArray<string> Segments => _segments;

    /// <summary>Segment cuối cùng: mã của thứ mà path này đặt tên.</summary>
    public string Code => _segments[^1];

    /// <summary>Mã enterprise, luôn là segment đầu tiên.</summary>
    public string EnterpriseCode => _segments[0];

    /// <summary>
    /// Nhà máy mà path này thuộc về, hoặc null với một path ở cấp enterprise.
    /// </summary>
    /// <remarks>
    /// Null chỉ có thể xảy ra ở đúng một cấp, và caller phải xử lý điều đó thay vì mặc định bỏ qua
    /// (AGENTS.md K3). Một enterprise trải rộng qua nhiều nhà máy, nên hỏi nó thuộc nhà máy nào không
    /// có câu trả lời — khác với mọi cấp bên dưới, nơi câu trả lời luôn bắt buộc.
    /// </remarks>
    public string? SiteId => _segments.Length > SiteSegmentIndex ? _segments[SiteSegmentIndex] : null;

    /// <summary>Path ở cấp trên một bậc, hoặc null khi đây đã là cấp enterprise.</summary>
    public EquipmentPath? Parent
    {
        get
        {
            if (_segments.Length == MinSegments)
            {
                return null;
            }

            var parentSegments = _segments.RemoveAt(_segments.Length - 1);

            return new EquipmentPath(string.Join(Separator, parentSegments), parentSegments);
        }
    }

    /// <summary>Parse một path, ném lỗi khi nó sai định dạng.</summary>
    /// <exception cref="FormatException">Chuỗi không mô tả một vị trí trong phân cấp.</exception>
    public static EquipmentPath Parse(string? value) =>
        TryParse(value, out var path)
            ? path
            : throw new FormatException("Not a valid equipment path: '" + value + "'.");

    /// <summary>Parse một path, trả về false khi nó sai định dạng.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out EquipmentPath? path)
    {
        path = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var segments = value.Split(Separator);

        // Dưới một segment thì không có gì để đặt tên; trên sáu thì không còn cấp nào để nó thuộc về.
        // Phân cấp bị cố định ở sáu bậc bởi docs/scope.md §2.1 — các thành phần con của một máy được mô
        // hình hoá thành thuộc tính của equipment, chứ không phải một cấp thứ bảy.
        if (segments.Length is < MinSegments or > MaxSegments)
        {
            return false;
        }

        if (Array.Exists(segments, segment => !IsValidSegment(segment)))
        {
            return false;
        }

        // Copy lại thay vì bọc trực tiếp: mảng mà Split trả về chỉ là biến local ở đây, nhưng một type
        // mà tính immutable của nó phụ thuộc vào việc không ai khác giữ mảng đó thì chỉ immutable nhờ
        // may mắn.
        path = new EquipmentPath(value, [.. segments]);
        return true;
    }

    /// <summary>Xây path của một con, ở cấp thấp hơn một bậc.</summary>
    /// <param name="childCode">Mã của con, ví dụ <c>FORM-01</c>.</param>
    /// <exception cref="FormatException">Mã sai định dạng.</exception>
    /// <exception cref="InvalidOperationException">Path này đã ở cấp sâu nhất.</exception>
    public EquipmentPath Append(string childCode)
    {
        if (!IsValidSegment(childCode))
        {
            throw new FormatException("Not a valid path segment: '" + childCode + "'.");
        }

        if (_segments.Length == MaxSegments)
        {
            throw new InvalidOperationException(
                $"'{Value}' is already {FactoryNodeKind.Equipment}, the deepest level of the hierarchy.");
        }

        return new EquipmentPath(
            string.Concat(Value, Separator.ToString(), childCode),
            _segments.Add(childCode));
    }

    /// <summary>Trả về toàn bộ path.</summary>
    public override string ToString() => Value;

    /// <summary>So sánh hai path theo văn bản của chúng.</summary>
    /// <remarks>
    /// Equality do record sinh ra sẽ compare segment theo reference và báo hai path giống hệt nhau là
    /// khác. Ordinal comparison cũng là lựa chọn đúng: đây là code máy, không phải từ ngữ; comparison
    /// theo culture có thể quyết định hai máy khác nhau cùng tên.
    /// </remarks>
    public bool Equals(EquipmentPath? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    private static bool IsValidSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        // Bắt đầu bằng chữ hoa và kết thúc bằng chữ cái hoặc chữ số. Từ chối segment rỗng do separator
        // lặp, dấu gạch ngang đầu/cuối, và mọi chữ thường.
        if (!char.IsAsciiLetterUpper(segment[0]) || segment[^1] == '-')
        {
            return false;
        }

        for (var index = 1; index < segment.Length; index++)
        {
            var character = segment[index];

            if (char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character))
            {
                continue;
            }

            // Một dấu gạch ngang trong code là bình thường — FORM-01-CH-0142. Dấu lặp là cách viết thứ
            // hai của cùng một máy đang chờ xảy ra.
            if (character == '-' && segment[index - 1] != '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
