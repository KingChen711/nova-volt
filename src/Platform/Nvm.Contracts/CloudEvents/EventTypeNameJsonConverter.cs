using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Ghi <see cref="EventTypeName"/> dưới dạng chuỗi đơn nhất đúng như nó trên wire, không phải dưới
/// dạng object.
/// </summary>
/// <remarks>
/// Không có converter này, type sẽ serialize thành
/// <c>{"value":"…","context":"…","name":"…","version":1}</c>, một cách render JSON hoàn toàn hợp lý
/// cho một C# record nhưng lại vi phạm đặc tả CloudEvents, nơi <c>type</c> là một string. Các phần đã
/// parse tồn tại để tiện cho code của chúng ta; chúng không phải một phần của contract.
/// </remarks>
internal sealed class EventTypeNameJsonConverter : JsonConverter<EventTypeName>
{
    public override EventTypeName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        // Từ chối ở đây thay vì để một type sai định dạng lọt qua là có chủ đích: một message có type
        // không hiểu được phải fail thật rõ ràng ngay tại boundary, nơi raw byte vẫn còn để nhìn vào,
        // thay vì trở thành một object xây dựng dở dang ở đâu đó bên dưới.
        return EventTypeName.TryParse(value, out var type)
            ? type
            : throw new JsonException("Not a valid CloudEvents type for this system: '" + value + "'.");
    }

    public override void Write(Utf8JsonWriter writer, EventTypeName value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStringValue(value.Value);
    }
}
