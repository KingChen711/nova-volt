using System.Collections.Frozen;
using System.Text.Json;
using MassTransit;
using Nvm.Bus.CloudEvents;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.EventStore;

namespace Nvm.Bus.Outbox;

/// <summary>Publishes the exact domain event stored in SQL with its original EventId.</summary>
public sealed class MassTransitEventOutboxPublisher(IPublishEndpoint endpoint) : IEventOutboxPublisher
{
    private static readonly FrozenDictionary<string, Type> EventTypes = DeclaredEventTypes.All()
        .ToFrozenDictionary(type => EventTypeName.Of(type).Value, type => type, StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(OutboxEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var domain = Deserialize(message);
        var headers = StoredCloudEventHeaders.Parse(message, domain);
        await endpoint.Publish(domain, domain.GetType(),
            new OutboxPublishPipe(message.EventId, headers), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rejects corrupt or unknown stored payloads before any broker side effect.</summary>
    public static IDomainEvent Deserialize(OutboxEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!EventTypes.TryGetValue(message.EventType, out var eventType))
        { throw new InvalidDataException($"No bus event contract for '{message.EventType}'."); }
        var parsed = EventTypeName.Parse(message.EventType);
        if (parsed.Version != message.SchemaVersion)
        { throw new InvalidDataException("Stored event type and schema version differ."); }
        var domain = JsonSerializer.Deserialize(message.PayloadJson, eventType, Json) as IDomainEvent
            ?? throw new InvalidDataException("Stored event payload could not be deserialized.");
        if (domain.EventId != message.EventId || !string.Equals(domain.SiteId, message.SiteId, StringComparison.Ordinal)
            || domain.OccurredAt != message.OccurredAt)
        { throw new InvalidDataException("Stored event identity, site or occurrence time differs from payload."); }
        return domain;
    }

    private sealed class OutboxPublishPipe(Guid eventId, StoredCloudEventHeaders headers) : IPipe<PublishContext>
    {
        public Task Send(PublishContext context)
        {
            context.MessageId = eventId;
            context.GetOrAddPayload(() => headers);
            headers.Apply(context);
            return Task.CompletedTask;
        }

        public void Probe(ProbeContext context) => context.CreateFilterScope("nvm-sql-event-outbox");
    }
}
