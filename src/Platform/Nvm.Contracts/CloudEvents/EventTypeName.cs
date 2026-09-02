using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Nvm.Contracts.Events;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Attribute <c>type</c> của CloudEvents: <c>com.novavolt.{context}.{event}.v{n}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ví dụ: <c>com.novavolt.traceability.unit-serialized.v1</c>, theo docs/scope.md §7.4.
/// </para>
/// <para>
/// Tiền tố reverse-DNS, rồi tới bounded context sở hữu event, rồi tới event viết kebab-case, rồi tới
/// schema version. Version là một phần của cái tên chứ không phải một field riêng vì một consumer
/// quyết định nó có đọc được một message hay không chỉ bằng cách nhìn vào một chuỗi duy nhất — và một
/// message không deserialize được thì chỉ còn lại header để nhìn vào.
/// </para>
/// <para>
/// Việc parse quan trọng không kém việc format. Khi một message rơi vào queue <c>_error</c>, theo
/// định nghĩa payload là thứ mà code không hiểu được; đọc lại <c>type</c> từ header là cách một
/// operator tìm ra nó đáng lẽ phải là gì.
/// </para>
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(EventTypeNameJsonConverter))]
public sealed record EventTypeName
{
    /// <summary>Tiền tố reverse-DNS chung cho mọi event type trong hệ thống.</summary>
    public const string Prefix = "com.novavolt";

    private const char Separator = '.';
    private const char VersionMarker = 'v';
    private const int SegmentCount = 5;
    private const int ContextSegment = 2;
    private const int NameSegment = 3;
    private const int VersionSegment = 4;

    private EventTypeName(string value, string context, string name, int version)
    {
        Value = value;
        Context = context;
        Name = name;
        Version = version;
    }

    /// <summary>Chuỗi type đầy đủ, đúng như nó di chuyển trên wire.</summary>
    public string Value { get; }

    /// <summary>Bounded context sở hữu event, ví dụ <c>traceability</c>.</summary>
    public string Context { get; }

    /// <summary>Tên event viết kebab-case, ví dụ <c>unit-serialized</c>.</summary>
    public string Name { get; }

    /// <summary>Schema version, bắt đầu từ 1. Khớp với <c>EventVersionAttribute</c> của type.</summary>
    public int Version { get; }

    /// <summary>Xây dựng một type name từ các phần của nó, ném lỗi khi có phần bị sai định dạng.</summary>
    /// <exception cref="FormatException">Một segment không phải kebab-case chữ thường, hoặc version dưới 1.</exception>
    public static EventTypeName Create(string? context, string? name, int version) =>
        TryCreate(context, name, version, out var type)
            ? type
            : throw new FormatException(
                $"Not a valid event type: context '{context}', name '{name}', version {version.ToString(CultureInfo.InvariantCulture)}.");

    /// <summary>Xây dựng một type name từ các phần của nó, trả về false khi có phần bị sai định dạng.</summary>
    public static bool TryCreate(
        [NotNullWhen(true)] string? context,
        [NotNullWhen(true)] string? name,
        int version,
        [NotNullWhen(true)] out EventTypeName? type)
    {
        type = null;

        if (!Tokens.IsFixedVocabulary(context) || !Tokens.IsFixedVocabulary(name) || version < 1)
        {
            return false;
        }

        var value = string.Concat(
            Prefix,
            Separator,
            context,
            Separator,
            name,
            Separator,
            VersionMarker,
            version.ToString(CultureInfo.InvariantCulture));

        type = new EventTypeName(value, context, name, version);
        return true;
    }

    /// <summary>Đọc lại tên trên wire mà một event type khai báo qua các attribute của nó.</summary>
    /// <param name="eventType">Một type mang <c>EventContract</c> và <c>EventVersion</c>.</param>
    /// <exception cref="InvalidOperationException">Thiếu một attribute, hoặc cặp attribute bị sai định dạng.</exception>
    /// <remarks>
    /// Nơi duy nhất ghép hai attribute lại thành chuỗi duy nhất di chuyển trên wire. Mọi thứ cần biết
    /// một event được gọi là gì trên wire — exchange nó publish tới, routing key, CloudEvents
    /// <c>type</c> — đều hỏi ở đây, nên không có phép suy dẫn thứ hai nào có thể lệch đi.
    /// </remarks>
    public static EventTypeName Of(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        var contract = eventType.GetCustomAttribute<EventContractAttribute>()
            ?? throw new InvalidOperationException(
                $"Event '{eventType.Name}' has no [EventContract]. Every event states its wire name.");

        var version = eventType.GetCustomAttribute<EventVersionAttribute>()
            ?? throw new InvalidOperationException(
                $"Event '{eventType.Name}' has no [EventVersion]. Every event is versioned from v1.");

        return TryCreate(contract.Context, contract.Name, version.Version, out var type)
            ? type
            : throw new InvalidOperationException(
                $"Event '{eventType.Name}' declares an invalid wire name: "
                + $"context '{contract.Context}', name '{contract.Name}', version {version.Version}.");
    }

    /// <summary>Đọc lại một type name từ wire, ném lỗi khi chuỗi bị sai định dạng.</summary>
    /// <exception cref="FormatException">Chuỗi không khớp với cấu trúc quy định.</exception>
    public static EventTypeName Parse(string? value) =>
        TryParse(value, out var type)
            ? type
            : throw new FormatException("Not a valid event type name: '" + value + "'.");

    /// <summary>Đọc lại một type name từ wire, trả về false khi chuỗi bị sai định dạng.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out EventTypeName? type)
    {
        type = null;

        if (value is null)
        {
            return false;
        }

        var segments = value.Split(Separator);

        if (segments.Length != SegmentCount)
        {
            return false;
        }

        if (!TryReadVersion(segments[VersionSegment], out var version))
        {
            return false;
        }

        if (!TryCreate(segments[ContextSegment], segments[NameSegment], version, out var candidate))
        {
            return false;
        }

        // Xây dựng lại rồi so sánh chính là điều biến đây thành một parser thay vì một reader dễ dãi.
        // Bất cứ gì sẽ round-trip ra một chuỗi khác — "v01", một tiền tố sai, một dấu cộng thừa mà bộ
        // parse số vẫn chấp nhận — đều bị từ chối ở đây thay vì bị âm thầm chuẩn hóa rồi lưu vào store.
        // Một event type phải chỉ có đúng một cách viết, mãi mãi.
        if (!string.Equals(candidate.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        type = candidate;
        return true;
    }

    /// <summary>Trả về chuỗi type đầy đủ.</summary>
    public override string ToString() => Value;

    private static bool TryReadVersion(string segment, out int version)
    {
        version = 0;

        if (segment.Length < 2 || segment[0] != VersionMarker)
        {
            return false;
        }

        return int.TryParse(
            segment.AsSpan(1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out version);
    }
}
