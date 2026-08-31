using System.Collections.Immutable;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.TelemetryBackfill;

/// <summary>Runs the online formation line over historical process time and decodes its wire output.</summary>
public sealed class TelemetryBackfillGenerator
{
    /// <summary>Emits exactly the readings the simulator's report-by-exception path would publish.</summary>
    public static IEnumerable<BackfillRow> Generate(TelemetryBackfillSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var drift = new DeviceClockDrift(spec.DriftedDeviceRate, spec.ClockDrift);
        var line = new FormationLine(
            spec.LinePath,
            spec.Channels,
            FormationProfile.Default,
            spec.StartAt,
            drift: drift);
        var pathsByCode = spec.Channels.ToDictionary(path => path.Code, StringComparer.Ordinal);
        var aliases = new Dictionary<string, MetricAliasTable>(StringComparer.Ordinal);
        long emitted = 0;

        foreach (var row in Decode(line.Connect(TimeSpan.Zero), spec.StartAt))
        {
            emitted++;
            yield return row;
        }

        for (var elapsed = spec.SamplePeriod; elapsed < spec.EndAt - spec.StartAt; elapsed += spec.SamplePeriod)
        {
            foreach (var row in Decode(line.Advance(elapsed), spec.StartAt + elapsed))
            {
                emitted++;
                yield return row;
            }
        }

        if (emitted != line.MeasurementCount)
        {
            throw new InvalidOperationException(
                $"FormationLine counted {line.MeasurementCount} readings but backfill decoded {emitted}.");
        }

        IEnumerable<BackfillRow> Decode(
            ImmutableArray<ComposedMessage> messages,
            DateTimeOffset gatewayTimestamp)
        {
            foreach (var composed in messages)
            {
                var topic = composed.Topic;

                if (topic.MessageType == SparkplugMessageType.NodeBirth)
                {
                    continue;
                }

                if (topic.DeviceCode is not { } deviceCode || !pathsByCode.TryGetValue(deviceCode, out var path))
                {
                    throw new InvalidOperationException($"Simulator emitted unknown device topic '{topic.Value}'.");
                }

                ImmutableArray<DeviceReading> readings;
                if (topic.MessageType == SparkplugMessageType.DeviceBirth)
                {
                    var birth = SparkplugPayload.DecodeBirth(composed.Payload.AsSpan());
                    aliases[path.Value] = birth.Aliases;
                    readings = birth.Readings;
                }
                else if (topic.MessageType == SparkplugMessageType.DeviceData)
                {
                    if (!aliases.TryGetValue(path.Value, out var table))
                    {
                        throw new InvalidOperationException($"Data for '{path.Value}' arrived before its birth.");
                    }

                    readings = SparkplugPayload.DecodeData(composed.Payload.AsSpan(), table);
                }
                else
                {
                    throw new InvalidOperationException($"Backfill does not accept '{topic.MessageType}'.");
                }

                foreach (var reading in readings)
                {
                    if (!SparkplugPayload.IsProtocolMetric(reading.MetricName))
                    {
                        yield return BackfillRow.From(path, reading, gatewayTimestamp, spec.RecordedAt);
                    }
                }
            }
        }
    }
}
