using System.Collections.Immutable;

namespace Nvm.Kernel.EventSourcing;

/// <summary>Site-scoped append and replay; append joins the current command transaction.</summary>
public interface IEventStore
{
    /// <summary>Appends with optimistic concurrency in the active command transaction; returns the final stream version.</summary>
    Task<long> AppendAsync(string siteId, string streamId, string streamType, long expectedVersion,
        ImmutableArray<NewStreamEvent> events, CancellationToken cancellationToken);

    /// <summary>Reads a site's ordered stream, applying configured schema upcasters to payloads.</summary>
    Task<EventStream?> ReadStreamAsync(string siteId, string streamId, CancellationToken cancellationToken);

    /// <summary>Returns the latest cached aggregate snapshot for the site and stream, if any.</summary>
    Task<EventSnapshot?> ReadSnapshotAsync(string siteId, string streamId, CancellationToken cancellationToken);

    /// <summary>Appends a snapshot at a valid stream boundary in the active command transaction.</summary>
    Task SaveSnapshotAsync(EventSnapshot snapshot, CancellationToken cancellationToken);
}
