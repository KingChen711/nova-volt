using System.Globalization;
using System.Text.Json;
using MassTransit;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.EventStore;

namespace Nvm.Bus.CloudEvents;

/// <summary>Validated SQL envelope attributes carried only by the outbox publish pipe.</summary>
internal sealed record StoredCloudEventHeaders(
    string SpecVersion, string Id, string Type, string Source, string Time,
    string DataContentType, string? Subject, string? DataSchema,
    string? CorrelationId, string? CausationId, string? PartitionKey)
{
    public void Apply(SendContext context)
    {
        context.Headers.Set(CloudEventHeaders.SpecVersion, SpecVersion);
        context.Headers.Set(CloudEventHeaders.Id, Id);
        context.Headers.Set(CloudEventHeaders.Type, Type);
        context.Headers.Set(CloudEventHeaders.Source, Source);
        context.Headers.Set(CloudEventHeaders.Time, Time);
        context.Headers.Set(CloudEventHeaders.DataContentType, DataContentType);
        SetOptional(context, "ce_subject", Subject);
        SetOptional(context, "ce_dataschema", DataSchema);
        SetOptional(context, "ce_correlationid", CorrelationId);
        SetOptional(context, "ce_causationid", CausationId);
        SetOptional(context, "ce_partitionkey", PartitionKey);
    }

    public static StoredCloudEventHeaders Parse(OutboxEvent stored, IDomainEvent domain)
    {
        try
        {
            using var envelope = JsonDocument.Parse(stored.CloudEventJson);
            using var payload = JsonDocument.Parse(stored.PayloadJson);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                !JsonElement.DeepEquals(data, payload.RootElement))
            {
                throw new InvalidDataException("Stored CloudEvents data differs from the event payload.");
            }

            var specVersion = Required(root, "specversion");
            var id = Required(root, "id");
            var type = Required(root, "type");
            var source = Required(root, "source");
            var time = Required(root, "time");
            var dataContentType = Required(root, "datacontenttype");
            if (specVersion != "1.0" ||
                !Guid.TryParse(id, out var parsedId) || parsedId != stored.EventId ||
                type != stored.EventType || type != EventTypeName.Of(domain.GetType()).Value ||
                !EventSource.TryParse(source, out var parsedSource) || parsedSource.SiteId != stored.SiteId ||
                !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsedTime) || parsedTime != stored.OccurredAt ||
                dataContentType != "application/json")
            {
                throw new InvalidDataException("Stored CloudEvents attributes differ from the event.");
            }

            var subject = Optional(root, "subject");
            var dataSchema = Optional(root, "dataschema");
            var correlationId = Optional(root, "correlationid");
            var causationId = Optional(root, "causationid");
            var partitionKey = Optional(root, "partitionkey");
            if (dataSchema is not null && !Uri.TryCreate(dataSchema, UriKind.Absolute, out _))
            {
                throw new InvalidDataException("Stored CloudEvents dataschema is not an absolute URI.");
            }

            return new StoredCloudEventHeaders(specVersion, id, type, source, time,
                dataContentType, subject, dataSchema, correlationId, causationId, partitionKey);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Stored CloudEvents envelope is not valid JSON.", error);
        }
    }

    private static string Required(JsonElement root, string name) =>
        Optional(root, name) ?? throw new InvalidDataException($"Stored CloudEvents '{name}' is missing.");

    private static string? Optional(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Stored CloudEvents '{name}' must be nonempty text.");
        }

        return value.GetString();
    }

    private static void SetOptional(SendContext context, string name, string? value)
    {
        if (value is not null)
        {
            context.Headers.Set(name, value);
        }
        else if (context.Headers.TryGetHeader(name, out _))
        {
            throw new InvalidDataException($"Header '{name}' is absent from the stored CloudEvents envelope.");
        }
    }
}
