using System.Collections.Immutable;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Simulator;

/// <summary>Counts measurements out of published payloads, the way the pipeline counts rows.</summary>
/// <remarks>
/// <para>
/// Decoded rather than tallied. A <c>DDATA</c> carries aliases alone and is unreadable without the
/// <c>DBIRTH</c> that declared them, so the alias tables are built up in publish order — counting raw
/// metric entries instead would count something the gateway could never forward and no database could
/// ever store.
/// </para>
/// <para>
/// Protocol metrics are left out on the same grounds the gateway leaves them out: <c>bdSeq</c> and the
/// control metrics are Sparkplug talking about its own session, no instrument measured them, and
/// counting them here would put the right-hand side of a reconciliation above the left by a fixed
/// amount per node birth.
/// </para>
/// <para>
/// Every message it is given is counted, duplicates included. That is deliberate — this stands in for
/// what a consumer received, so a caller who wants the plant's own number has to feed it a stream
/// with no injected duplicates in it.
/// </para>
/// </remarks>
internal sealed class MeasurementLedger
{
    private readonly Dictionary<string, MetricAliasTable> _aliases = new(StringComparer.Ordinal);

    /// <summary>How many measurements everything added so far carried.</summary>
    public long Total { get; private set; }

    /// <summary>Reads one message and adds what a consumer could have stored out of it.</summary>
    public void Add(SparkplugMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var key = message.Topic.DeviceCode ?? string.Empty;
        ImmutableArray<DeviceReading> readings;

        if (message.Topic.MessageType is SparkplugMessageType.NodeBirth or SparkplugMessageType.DeviceBirth)
        {
            var birth = SparkplugPayload.DecodeBirth(message.Payload.AsSpan());

            _aliases[key] = birth.Aliases;
            readings = birth.Readings;
        }
        else
        {
            readings = SparkplugPayload.DecodeData(
                message.Payload.AsSpan(),
                _aliases.GetValueOrDefault(key, MetricAliasTable.Empty));
        }

        Total += readings.Count(reading => !SparkplugPayload.IsProtocolMetric(reading.MetricName));
    }

    /// <summary>Reads a run of messages, in the order they went out.</summary>
    public void AddAll(IEnumerable<SparkplugMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var message in messages)
        {
            Add(message);
        }
    }
}
