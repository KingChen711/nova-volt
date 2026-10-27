using System.Collections.Concurrent;

namespace Nvm.Foundation.Runtime;

/// <summary>Event đã lưu cùng transaction, sẵn sàng phát.</summary>
public sealed record PublishedEvent(Guid EventId, string EventType, int Version, string Payload, Guid CorrelationId, IFoundationEvent Event);

/// <summary>Phát event sau commit. R1: trong process; R2: lên RabbitMQ và subscription.</summary>
public interface IEventPublisher
{
    void Publish(IReadOnlyList<PublishedEvent> events);
}

public sealed class InMemoryEventPublisher : IEventPublisher
{
    private readonly ConcurrentQueue<PublishedEvent> _published = new();

    public IReadOnlyList<PublishedEvent> Published => [.. _published];

    public void Publish(IReadOnlyList<PublishedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events)
        {
            _published.Enqueue(e);
        }
    }
}
