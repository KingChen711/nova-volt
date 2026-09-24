namespace Nvm.Kernel.EventSourcing;

/// <summary>The stream advanced after the caller loaded its expected version.</summary>
public sealed class EventConcurrencyException(string siteId, string streamId, long expectedVersion, long actualVersion)
    : Exception($"Stream {siteId}/{streamId} expected version {expectedVersion}, actual version {actualVersion}.")
{
    public string SiteId { get; } = siteId;
    public string StreamId { get; } = streamId;
    public long ExpectedVersion { get; } = expectedVersion;
    public long ActualVersion { get; } = actualVersion;
}
