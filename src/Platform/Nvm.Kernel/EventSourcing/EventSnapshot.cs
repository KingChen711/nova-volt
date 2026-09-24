namespace Nvm.Kernel.EventSourcing;

/// <summary>An optional cached aggregate state at a known stream version.</summary>
public sealed record EventSnapshot(
    string SiteId,
    string StreamId,
    long Version,
    string StateJson,
    DateTimeOffset RecordedAt);
