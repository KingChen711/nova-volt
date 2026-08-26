using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Writes <see cref="EventSource"/> as the URN string it is on the wire, not as an object.
/// </summary>
/// <remarks>
/// Same reason as <see cref="EventTypeNameJsonConverter"/>. Note that the round trip is not a plain
/// string copy: the URN carries the site in lower case and the parsed object holds it in canonical
/// upper case, so reading is where that translation happens.
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
