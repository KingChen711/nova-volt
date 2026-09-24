using Nvm.Kernel.EventSourcing;

namespace Nvm.EventStore;

/// <summary>Deterministically advances historical JSON through every declared schema step.</summary>
public sealed class EventUpcasterChain
{
    private readonly Dictionary<(string EventType, int Version), IEventUpcaster> _steps;
    private readonly Dictionary<string, int> _currentVersions;

    public EventUpcasterChain(IEnumerable<IEventUpcaster> upcasters)
    {
        ArgumentNullException.ThrowIfNull(upcasters);
        _steps = new Dictionary<(string, int), IEventUpcaster>();
        _currentVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in upcasters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(step.EventType);
            if (step.FromVersion < 1 || step.ToVersion != step.FromVersion + 1)
            { throw new ArgumentException("An upcaster must advance exactly one positive schema version.", nameof(upcasters)); }
            if (!_steps.TryAdd((step.EventType, step.FromVersion), step))
            { throw new ArgumentException("Duplicate upcaster step.", nameof(upcasters)); }
            _currentVersions[step.EventType] = Math.Max(_currentVersions.GetValueOrDefault(step.EventType), step.ToVersion);
        }
    }

    public (int Version, string PayloadJson) Upcast(string eventType, int storedVersion, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentOutOfRangeException.ThrowIfLessThan(storedVersion, 1);
        if (!_currentVersions.TryGetValue(eventType, out var currentVersion))
        { return (storedVersion, payloadJson); }
        if (storedVersion > currentVersion)
        { throw new InvalidOperationException($"Event {eventType} v{storedVersion} is newer than this reader (v{currentVersion})."); }

        var version = storedVersion;
        var json = payloadJson;
        while (version < currentVersion)
        {
            if (!_steps.TryGetValue((eventType, version), out var step))
            { throw new InvalidOperationException($"Missing upcaster for {eventType} v{version} to v{version + 1}."); }
            json = step.Upcast(json);
            ArgumentException.ThrowIfNullOrWhiteSpace(json);
            version = step.ToVersion;
        }
        return (version, json);
    }
}
