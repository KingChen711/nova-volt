using System.Collections.Immutable;
using Nvm.Kernel.EventSourcing;

namespace Nvm.UnitTests.TestSupport;

/// <summary>Event store trong RAM cho unit test: optimistic concurrency theo version, không transaction.</summary>
public sealed class MemoryEventStore : IEventStore
{
    private readonly Dictionary<(string, string), EventStream> _streams = new();
    private readonly Dictionary<(string, string), EventSnapshot> _snapshots = new();
    private long _sequence;

    public IReadOnlyList<StoredStreamEvent> All => [.. _streams.Values.SelectMany(s => s.Events).OrderBy(e => e.GlobalSequence)];

    public Task<long> AppendAsync(string siteId, string streamId, string streamType, long expectedVersion,
        ImmutableArray<NewStreamEvent> events, CancellationToken cancellationToken)
    {
        _streams.TryGetValue((siteId, streamId), out var old);
        var version = old?.Version ?? 0;
        if (version != expectedVersion)
        { throw new EventConcurrencyException(siteId, streamId, expectedVersion, version); }
        var builder = (old?.Events ?? []).ToBuilder();
        foreach (var value in events)
        {
            builder.Add(new StoredStreamEvent(++_sequence, siteId, streamId, ++version, value.SourceEventId,
                value.EventType, value.SchemaVersion, value.PayloadJson, value.MetadataJson, value.OccurredAt,
                value.RecordedAt));
        }
        _streams[(siteId, streamId)] = new EventStream(siteId, streamId, streamType, version, builder.ToImmutable());
        return Task.FromResult(version);
    }

    public Task<EventStream?> ReadStreamAsync(string siteId, string streamId, CancellationToken cancellationToken)
    {
        _streams.TryGetValue((siteId, streamId), out var value);
        return Task.FromResult(value);
    }

    public Task<EventSnapshot?> ReadSnapshotAsync(string siteId, string streamId, CancellationToken cancellationToken)
    {
        _snapshots.TryGetValue((siteId, streamId), out var value);
        return Task.FromResult(value);
    }

    public Task SaveSnapshotAsync(EventSnapshot snapshot, CancellationToken cancellationToken)
    {
        _snapshots[(snapshot.SiteId, snapshot.StreamId)] = snapshot;
        return Task.CompletedTask;
    }
}
