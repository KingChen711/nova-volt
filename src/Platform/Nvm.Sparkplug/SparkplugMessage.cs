using System.Collections.Immutable;
using Nvm.Sparkplug.Topics;

namespace Nvm.Sparkplug;

/// <summary>One MQTT publish: where it goes and what it carries.</summary>
/// <param name="Topic">The topic, which is the only place the address lives.</param>
/// <param name="Payload">The encoded Sparkplug B payload.</param>
/// <remarks>
/// The two travel together because neither means anything alone — a payload with no topic names no
/// machine, and this is the pair a publisher hands to a broker and a subscriber receives back.
/// </remarks>
public sealed record SparkplugMessage(SparkplugTopic Topic, ImmutableArray<byte> Payload)
{
    /// <summary>Compares the topic and the bytes.</summary>
    /// <remarks>
    /// <see cref="ImmutableArray{T}"/> compares by the identity of the array it wraps, so the record's
    /// generated equality would call two identical publishes different. That would be invisible until
    /// a test comparing an expected message to an actual one started failing for no reason anybody
    /// could see in the values.
    /// </remarks>
    public bool Equals(SparkplugMessage? other) =>
        other is not null
        && Topic == other.Topic
        && Payload.AsSpan().SequenceEqual(other.Payload.AsSpan());

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Topic, Payload.Length);
}
