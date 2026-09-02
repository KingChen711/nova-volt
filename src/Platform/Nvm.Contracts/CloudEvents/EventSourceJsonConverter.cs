using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Ghi <see cref="EventSource"/> dưới dạng chuỗi URN đúng như nó trên wire, không phải dưới dạng object.
/// </summary>
/// <remarks>
/// Cùng lý do như <see cref="EventTypeNameJsonConverter"/>. Lưu ý round trip không phải một phép sao
/// chép chuỗi đơn thuần: URN mang site ở dạng chữ thường còn object đã parse thì giữ nó ở dạng chuẩn
/// chữ hoa, nên bước đọc chính là nơi diễn ra sự chuyển đổi đó.
/// </remarks>
internal sealed class EventSourceJsonConverter : JsonConverter<EventSource>
{
    public override EventSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        return EventSource.TryParse(value, out var source)
            ? source
            : throw new JsonException("Not a valid CloudEvents source for this system: '" + value + "'.");
    }

    public override void Write(Utf8JsonWriter writer, EventSource value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStringValue(value.Value);
    }
}
