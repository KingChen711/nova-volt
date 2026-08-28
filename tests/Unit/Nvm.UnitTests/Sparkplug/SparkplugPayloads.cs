using Google.Protobuf;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Builds Sparkplug payloads in-process, for the cases a captured fixture cannot cover.</summary>
/// <remarks>
/// <para>
/// This is the encoder the fixtures in <c>tests/Fixtures/sparkplug/</c> deliberately avoid, so it is
/// worth being explicit about why it is allowed here. Those fixtures answer <i>"is our copy of the
/// schema the same one the devices use"</i>, and a self-encoded payload cannot answer that. These
/// answer <i>"given a payload of this shape, what does the decoder produce"</i> — a question about our
/// mapping, on a schema the fixtures and <c>SparkplugPinTests</c> have already pinned.
/// </para>
/// <para>
/// The generated <c>Org.Eclipse.Tahu.Protobuf</c> types appear here and nowhere else in the test
/// project. That is the same boundary ADR-026 draws for production code, kept visible by keeping the
/// crossing in one file.
/// </para>
/// </remarks>
internal static class SparkplugPayloads
{
    /// <summary>2026-08-28T07:15:30.500Z — the same instant the captured fixtures use.</summary>
    internal const ulong DefaultTimestampMs = 1787901330500;

    /// <summary>Encodes a payload exactly as given, including the parts that are missing.</summary>
    internal static byte[] Encode(ulong? timestamp, ulong? seq, params SparkplugMetric[] metrics)
    {
        var payload = new Payload();

        if (timestamp is { } stamped)
        {
            payload.Timestamp = stamped;
        }

        if (seq is { } sequence)
        {
            payload.Seq = sequence;
        }

        payload.Metrics.AddRange(metrics);

        return payload.ToByteArray();
    }

    /// <summary>A payload carrying one metric, with the payload timestamp set.</summary>
    internal static byte[] Carrying(SparkplugMetric metric) =>
        Encode(DefaultTimestampMs, seq: 1, metric);

    /// <summary>A birth that declares the given names and aliases, all of them Float.</summary>
    /// <remarks>
    /// Enough for the tests that are about <i>which</i> aliases a table holds rather than about
    /// datatypes; <c>SparkplugValueTests</c> builds metrics one at a time when the type is the point.
    /// </remarks>
    internal static byte[] BirthDeclaring(params (string Name, ulong Alias)[] metrics)
    {
        var built = Array.ConvertAll(metrics, metric => new SparkplugMetric
        {
            Name = metric.Name,
            Alias = metric.Alias,
            Datatype = (uint)DataType.Float,
            Timestamp = DefaultTimestampMs,
            FloatValue = 0f,
        });

        return Encode(DefaultTimestampMs, seq: 0, built);
    }
}
