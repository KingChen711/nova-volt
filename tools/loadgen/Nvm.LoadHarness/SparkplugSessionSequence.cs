namespace Nvm.LoadHarness;

/// <summary>Allocates the one ordered Sparkplug sequence owned by an edge-node session.</summary>
/// <remarks>
/// The owner calls this from one scheduler. Concurrency belongs in the MQTT in-flight window, after
/// the message has received its sequence; it must not create a second sequence stream.
/// </remarks>
public sealed class SparkplugSessionSequence
{
    private ulong _next;

    /// <summary>Returns the next value and advances modulo the Sparkplug 8-bit sequence range.</summary>
    public ulong TakeNext()
    {
        var current = _next;
        _next = (current + 1) % 256;
        return current;
    }
}
