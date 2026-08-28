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
    internal static byte[] BirthDeclaring(params (string Name, ulong Alias)[] metrics) =>
        BirthDeclaring(seq: 0, metrics);

    /// <summary>The same birth at a chosen sequence number, for tests about <c>seq</c> continuity.</summary>
    internal static byte[] BirthDeclaring(ulong seq, params (string Name, ulong Alias)[] metrics)
    {
        var built = Array.ConvertAll(metrics, metric => new SparkplugMetric
        {
            Name = metric.Name,
            Alias = metric.Alias,
            Datatype = (uint)DataType.Float,
            Timestamp = DefaultTimestampMs,
            FloatValue = 0f,
        });

        return Encode(DefaultTimestampMs, seq, built);
    }

    /// <summary>An <c>NBIRTH</c> declaring a session number and nothing else of interest.</summary>
    internal static byte[] NodeBirth(ulong birthDeathSequence) =>
        Encode(DefaultTimestampMs, seq: 0, BirthDeathSequenceMetric(birthDeathSequence));

    /// <summary>An <c>NDEATH</c> as a broker publishes it from a registered will.</summary>
    internal static byte[] NodeDeath(ulong? birthDeathSequence) =>
        birthDeathSequence is { } session
            ? Encode(DefaultTimestampMs, seq: null, BirthDeathSequenceMetric(session))
            : Encode(DefaultTimestampMs, seq: null);

    /// <summary>A data payload for one declared alias, at a chosen sequence number.</summary>
    internal static byte[] DataAt(ulong seq, ulong alias, float value) =>
        Encode(
            DefaultTimestampMs,
            seq,
            new SparkplugMetric
            {
                Alias = alias,
                Timestamp = DefaultTimestampMs,
                FloatValue = value,
            });

    private static SparkplugMetric BirthDeathSequenceMetric(ulong birthDeathSequence) =>
        new()
        {
            // No alias, by specification: a will is composed at connect time, before the birth that
            // would have assigned one.
            Name = "bdSeq",
            Datatype = (uint)DataType.Int64,
            Timestamp = DefaultTimestampMs,
            LongValue = birthDeathSequence,
        };
}
