using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Writes <see cref="EventTypeName"/> as the single string it is on the wire, not as an object.
/// </summary>
/// <remarks>
/// Without this the type would serialize as <c>{"value":"…","context":"…","name":"…","version":1}</c>,
/// which is a perfectly reasonable JSON rendering of a C# record and a violation of the CloudEvents
/// specification, where <c>type</c> is a string. The parsed parts exist for our convenience in code;
/// they are not part of the contract.
/// </remarks>
internal sealed class EventTypeNameJsonConverter : JsonConverter<EventTypeName>
{
    public override EventTypeName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        // Rejecting here rather than letting a malformed type through is deliberate: a message whose
        // type cannot be understood must fail loudly at the boundary, where the raw bytes are still
        // available to look at, instead of becoming a half-built object somewhere downstream.
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
