using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Routing key AMQP mà một event được publish với nó: <c>nvm.{site}.{context}.{event}.v{n}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ví dụ: <c>nvm.NV1.traceability.unit-serialized.v1</c>, theo docs/scope.md §7.4.
/// </para>
/// <para>
/// Hình dạng này tồn tại để một consumer có thể subscribe đúng cái nó cần — một plant, một context,
/// một version — mà không phải nhận mọi thứ rồi lọc bằng code.
/// </para>
/// <para>
/// Site giữ nguyên chữ hoa ở đây, trong khi source URN lại viết thường nó. Quy tắc đứng sau cả hai là
/// như nhau: fixed vocabulary được viết thường, còn identifier giữ dạng canonical của nó. Site code là
/// một identifier, và dạng canonical của nó là chữ hoa ở mọi nơi khác trong hệ thống — trong serial
/// number, trong equipment path, trong claim <c>site_id</c> đến từ Keycloak.
/// </para>
/// <para>
/// AMQP so sánh routing key theo từng byte. Một publisher viết <c>nvm.NV1.…</c> và một consumer bind
/// <c>nvm.nv1.#</c> sẽ không khớp nhau, và không có gì báo lại điều đó ở bất kỳ đâu: message tới được
/// exchange, không khớp binding nào, rồi biến mất. Lỗi này im lặng cho đến khi ai đó thắc mắc vì sao
/// một report trông ngắn bất thường. Vì vậy type này là cách duy nhất để dựng chuỗi đó, và nó từ chối
/// một site viết thường thay vì âm thầm chấp nhận nó.
/// </para>
/// </remarks>
public sealed record RoutingKey
{
    /// <summary>Prefix mà mọi routing key trong hệ thống dùng chung.</summary>
    public const string Prefix = "nvm";

    private const char Separator = '.';
    private const char VersionMarker = 'v';
    private const int SegmentCount = 5;
    private const int SiteSegment = 1;
    private const int ContextSegment = 2;
    private const int NameSegment = 3;
    private const int VersionSegment = 4;

    private RoutingKey(string value, string siteId, EventTypeName eventType)
    {
        Value = value;
        SiteId = siteId;
        EventType = eventType;
    }

    /// <summary>Routing key đầy đủ, đúng như khi được publish.</summary>
    public string Value { get; }

    /// <summary>Site code ở dạng canonical chữ hoa, ví dụ <c>NV1</c>.</summary>
    public string SiteId { get; }

    /// <summary>
    /// Event type mà key này route tới. Giữ nguyên khối thay vì rời rạc thành các string, để routing
    /// key và attribute <c>type</c> của CloudEvents trên cùng một message không thể trôi lệch nhau.
    /// </summary>
    public EventTypeName EventType { get; }

    /// <summary>Dựng một routing key, ném lỗi khi site sai định dạng.</summary>
    /// <exception cref="FormatException">Site không phải chữ hoa-số.</exception>
    public static RoutingKey Create(string? siteId, EventTypeName eventType) =>
        TryCreate(siteId, eventType, out var key)
            ? key
            : throw new FormatException($"Not a valid routing key site: '{siteId}'. Site codes are upper case.");

    /// <summary>Dựng một routing key, trả về false khi site sai định dạng.</summary>
    public static bool TryCreate(
        [NotNullWhen(true)] string? siteId,
        EventTypeName eventType,
        [NotNullWhen(true)] out RoutingKey? key)
    {
        key = null;

        ArgumentNullException.ThrowIfNull(eventType);

        if (!Tokens.IsSiteCode(siteId))
        {
            return false;
        }

        var value = string.Join(
            Separator,
            Prefix,
            siteId,
            eventType.Context,
            eventType.Name,
            VersionMarker + eventType.Version.ToString(CultureInfo.InvariantCulture));

        key = new RoutingKey(value, siteId, eventType);
        return true;
    }

    /// <summary>Đọc lại một routing key từ broker, ném lỗi khi chuỗi sai định dạng.</summary>
    /// <exception cref="FormatException">Chuỗi không khớp layout.</exception>
    public static RoutingKey Parse(string? value) =>
        TryParse(value, out var key)
            ? key
            : throw new FormatException("Not a valid routing key: '" + value + "'.");

    /// <summary>Đọc lại một routing key từ broker, trả về false khi chuỗi sai định dạng.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out RoutingKey? key)
    {
        key = null;

        if (value is null)
        {
            return false;
        }

        var segments = value.Split(Separator);

        if (segments.Length != SegmentCount)
        {
            return false;
        }

        var versionSegment = segments[VersionSegment];

        if (versionSegment.Length < 2 || versionSegment[0] != VersionMarker)
        {
            return false;
        }

        if (!int.TryParse(versionSegment.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return false;
        }

        if (!EventTypeName.TryCreate(segments[ContextSegment], segments[NameSegment], version, out var eventType))
        {
            return false;
        }

        if (!TryCreate(segments[SiteSegment], eventType, out var candidate))
        {
            return false;
        }

        // Cùng lý do như EventTypeName.TryParse: dựng lại rồi so sánh, để một prefix sai hay một site
        // viết thường bị từ chối thay vì được "sửa" thành thứ không còn khớp với những gì publisher
        // thực sự đã gửi.
        if (!string.Equals(candidate.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>Trả về routing key đầy đủ.</summary>
    public override string ToString() => Value;
}
