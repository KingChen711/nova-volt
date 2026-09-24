using System.Text;
using System.Text.Json;
using Nvm.Contracts.CloudEvents;
using Nvm.Kernel.EventSourcing;

namespace Nvm.EventStore;

/// <summary>Builds the immutable JSON stored beside each event's replay payload.</summary>
internal static class StoredCloudEventEnvelope
{
    public static string Serialize(string siteId, NewStreamEvent item, string applicationName)
    {
        var eventType = EventTypeName.Parse(item.EventType);
        if (eventType.Version != item.SchemaVersion)
        {
            throw new ArgumentException("CloudEvents type version does not match the event schema version.", nameof(item));
        }

        var source = EventSource.Create(siteId, applicationName);
        using var payload = JsonDocument.Parse(item.PayloadJson);
        using var metadata = JsonDocument.Parse(item.MetadataJson);
        if (payload.RootElement.ValueKind != JsonValueKind.Object ||
            metadata.RootElement.ValueKind != JsonValueKind.Object ||
            !payload.RootElement.TryGetProperty("siteId", out var payloadSite) ||
            payloadSite.ValueKind != JsonValueKind.String ||
            !string.Equals(payloadSite.GetString(), siteId, StringComparison.Ordinal))
        {
            throw new ArgumentException("CloudEvents data must be an object with the matching siteId.", nameof(item));
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("specversion", "1.0");
            writer.WriteString("id", item.SourceEventId);
            writer.WriteString("type", eventType.Value);
            writer.WriteString("source", source.Value);
            WriteOptional(writer, metadata.RootElement, "subject");
            writer.WriteString("time", item.OccurredAt);
            writer.WriteString("datacontenttype", "application/json");
            WriteOptional(writer, metadata.RootElement, "dataschema");
            WriteOptional(writer, metadata.RootElement, "correlationid");
            WriteOptional(writer, metadata.RootElement, "causationid");
            WriteOptional(writer, metadata.RootElement, "partitionkey");
            writer.WritePropertyName("data");
            payload.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static void WriteOptional(Utf8JsonWriter writer, JsonElement metadata, string name)
    {
        if (!metadata.TryGetProperty(name, out var value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"CloudEvents metadata '{name}' must be a nonempty string when supplied.", nameof(metadata));
        }

        var text = value.GetString()!;
        if (name == "dataschema" && !Uri.TryCreate(text, UriKind.Absolute, out _))
        {
            throw new ArgumentException("CloudEvents dataschema must be an absolute URI.", nameof(metadata));
        }

        writer.WriteString(name, text);
    }
}
