using System.Diagnostics.CodeAnalysis;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Attribute <c>source</c> của CloudEvents: <c>urn:novavolt:{site}:{application}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ví dụ: <c>urn:novavolt:nv1:app-execution</c>, theo docs/scope.md §7.4.
/// </para>
/// <para>
/// Source trả lời câu "ai nói vậy". Cùng với event id, đây là thứ CloudEvents dùng để định nghĩa tính
/// duy nhất, và khi hai service bất đồng về cùng một unit thì đây là thứ đầu tiên ai đó nhìn vào.
/// </para>
/// <para>
/// Site xuất hiện ở đây dưới dạng chữ thường, còn trong routing key thì dưới dạng chữ hoa. Đó không
/// phải là sơ suất — xem <see cref="RoutingKey"/>. Bên trong type này, site luôn được giữ ở dạng chuẩn
/// (canonical) chữ hoa, để <see cref="SiteId"/> có thể được so sánh với mọi SiteId khác trong codebase
/// mà không ai phải nhớ chuỗi nào đến từ một URN.
/// </para>
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(EventSourceJsonConverter))]
public sealed record EventSource
{
    /// <summary>Tiền tố URN chung cho mọi source trong hệ thống.</summary>
    public const string Prefix = "urn:novavolt";

    private const char Separator = ':';
    private const int SegmentCount = 4;
    private const int SiteSegment = 2;
    private const int ApplicationSegment = 3;

    private EventSource(string value, string siteId, string application)
    {
        Value = value;
        SiteId = siteId;
        Application = application;
    }

    /// <summary>URN đầy đủ, đúng như nó di chuyển trên wire, với site ở dạng chữ thường.</summary>
    public string Value { get; }

    /// <summary>Mã site ở dạng chuẩn chữ hoa, ví dụ <c>NV1</c>.</summary>
    public string SiteId { get; }

    /// <summary>Deployable đã publish event này, ví dụ <c>app-execution</c>.</summary>
    public string Application { get; }

    /// <summary>Xây dựng một source URN, ném lỗi khi có phần bị sai định dạng.</summary>
    /// <exception cref="FormatException">Site không phải chữ hoa-số, hoặc application không phải kebab-case.</exception>
    public static EventSource Create(string? siteId, string? application) =>
        TryCreate(siteId, application, out var source)
            ? source
            : throw new FormatException($"Not a valid event source: site '{siteId}', application '{application}'.");

    /// <summary>Xây dựng một source URN, trả về false khi có phần bị sai định dạng.</summary>
    public static bool TryCreate(
        [NotNullWhen(true)] string? siteId,
        [NotNullWhen(true)] string? application,
        [NotNullWhen(true)] out EventSource? source)
    {
        source = null;

        if (!Tokens.IsSiteCode(siteId) || !Tokens.IsFixedVocabulary(application))
        {
            return false;
        }

        // ToLowerInvariant, không bao giờ dùng ToLower: khi InvariantGlobalization tắt (ADR-020) thì
        // ambient culture là có thật, và trên một máy Thổ Nhĩ Kỳ, ToLower biến 'I' thành 'ı'. Một
        // source URN mà phụ thuộc vào nơi tiến trình đang chạy thì không còn là một định danh nữa.
        var value = string.Concat(Prefix, Separator, siteId.ToLowerInvariant(), Separator, application);

        source = new EventSource(value, siteId, application);
        return true;
    }

    /// <summary>Đọc lại một source URN từ wire, ném lỗi khi chuỗi bị sai định dạng.</summary>
    /// <exception cref="FormatException">Chuỗi không khớp với cấu trúc quy định.</exception>
    public static EventSource Parse(string? value) =>
        TryParse(value, out var source)
            ? source
            : throw new FormatException("Not a valid event source: '" + value + "'.");

    /// <summary>Đọc lại một source URN từ wire, trả về false khi chuỗi bị sai định dạng.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out EventSource? source)
    {
        source = null;

        if (value is null)
        {
            return false;
        }

        var segments = value.Split(Separator);

        if (segments.Length != SegmentCount)
        {
            return false;
        }

        // Site di chuyển ở dạng chữ thường, nên nó được nâng trở lại dạng chuẩn trước khi validate.
        // Chuyển chữ hoa ở đây an toàn vì một mã site theo định nghĩa là chữ-số; bất cứ gì chưa từng ở
        // dạng chữ thường sẽ fail ở bước kiểm tra round-trip bên dưới.
        if (!TryCreate(segments[SiteSegment].ToUpperInvariant(), segments[ApplicationSegment], out var candidate))
        {
            return false;
        }

        if (!string.Equals(candidate.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        source = candidate;
        return true;
    }

    /// <summary>Trả về URN đầy đủ.</summary>
    public override string ToString() => Value;
}
