namespace Nvm.Kernel.EventSourcing;

/// <summary>A persisted stream fact. PayloadJson is upgraded to the current schema when read.</summary>
public sealed record StoredStreamEvent(
    long GlobalSequence,
    string SiteId,
    string StreamId,
    long Version,
    Guid SourceEventId,
    string EventType,
    int SchemaVersion,
    string PayloadJson,
    string MetadataJson,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt)
{
    /// <summary>The original, immutable CloudEvents 1.0 JSON; PayloadJson may be upcast on read.</summary>
    public string EnvelopeJson { get; init; } = "";
}
