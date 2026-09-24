namespace Nvm.Kernel.EventSourcing;

/// <summary>One deterministic conversion from an immutable historical event payload to its next schema.</summary>
public interface IEventUpcaster
{
    string EventType { get; }
    int FromVersion { get; }
    int ToVersion { get; }
    string Upcast(string payloadJson);
}
