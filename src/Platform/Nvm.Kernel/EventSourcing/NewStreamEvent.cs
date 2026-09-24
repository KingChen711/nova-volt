namespace Nvm.Kernel.EventSourcing;

/// <summary>An immutable fact ready to append to one aggregate stream.</summary>
public sealed record NewStreamEvent(
    Guid SourceEventId,
    string EventType,
    int SchemaVersion,
    string PayloadJson,
    string MetadataJson,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt);
