using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Simulator.Formation;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>
/// Lab phá hoại #2 of scope.md §9/M2, written as a repeatable measurement rather than a one-off.
/// </summary>
/// <remarks>
/// <para>
/// The lesson is not that dropping <c>device_timestamp</c> from the natural key breaks something.
/// It is that it breaks something <b>silently</b>: deduplication reports success, every insert
/// returns without an error, and the only symptom is that the table holds fewer rows than the plant
/// measured. This is the one class of defect where a green test suite means nothing without a count.
/// </para>
/// <para>
/// Measured on real simulator output rather than synthetic keys, because the shape of the data is
/// the whole question: how many readings share an equipment path and a signal code and differ only
/// in when they were taken.
/// </para>
/// </remarks>
public sealed class NaturalKeyWithoutDeviceTimestampTests
{
    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 29, 7, 0, 0, TimeSpan.Zero);

    // A fixed instant standing in for "the key does not carry a device timestamp". The value is
    // irrelevant; what matters is that every reading shares it.
    private static readonly DateTimeOffset NoTimestamp = DateTimeOffset.UnixEpoch;

    [Fact]
    public void DroppingDeviceTimestampFromTheKey_SwallowsAlmostEveryMeasurement()
    {
        var readings = RunOneCycle();

        readings.Count.ShouldBeGreaterThan(10_000, "the lab is stated over ten thousand measurements");

        var withTimestamp = readings
            .Select(reading => MeasurementNaturalKey.For(
                reading.EquipmentPath,
                ProcessStepCode.FromEquipmentPath(reading.EquipmentPath)!,
                reading.Reading.MetricName,
                reading.Reading.DeviceTimestamp).SourceEventId)
            .ToHashSet();

        var withoutTimestamp = readings
            .Select(reading => MeasurementNaturalKey.For(
                reading.EquipmentPath,
                ProcessStepCode.FromEquipmentPath(reading.EquipmentPath)!,
                reading.Reading.MetricName,
                NoTimestamp).SourceEventId)
            .ToHashSet();

        // Every measurement keeps its own identity when the instant is part of the key.
        withTimestamp.Count.ShouldBe(readings.Count);

        // Without it, identity collapses to (channel, signal) — one row per channel per signal, for
        // the whole eighteen-hour cycle. A formation curve becomes a single point.
        var swallowed = readings.Count - withoutTimestamp.Count;

        // Printed, not only asserted. This is a lab: the number is the result, and benchmarks.md
        // needs it verbatim rather than a re-derivation from an inequality.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"measurements={readings.Count} keys_with_timestamp={withTimestamp.Count} "
            + $"keys_without={withoutTimestamp.Count} swallowed={swallowed} "
            + $"({100.0 * swallowed / readings.Count:F2}%)");

        swallowed.ShouldBeGreaterThan(readings.Count * 99 / 100);
        withoutTimestamp.Count.ShouldBe(ChannelCount * SignalCount);
    }

    private const int ChannelCount = 8;
    private const int SignalCount = 6;

    private static List<(EquipmentPath EquipmentPath, DeviceReading Reading)> RunOneCycle()
    {
        var channels = Enumerable.Range(1, ChannelCount)
            .Select(index => EquipmentPath.Parse(
                $"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-{index:0000}"))
            .ToArray();

        var line = new FormationLine(LinePath, channels, FormationProfile.Default, StartedAt);
        var readings = new List<(EquipmentPath, DeviceReading)>();
        var aliases = new Dictionary<string, MetricAliasTable>(StringComparer.Ordinal);

        Collect(line.Connect(TimeSpan.Zero), readings, aliases);

        // Five-second samples over the whole cycle: the sample period the plant actually runs at,
        // so the ratio being measured is the plant's ratio and not one chosen to make a point.
        for (var elapsed = TimeSpan.FromSeconds(5);
            elapsed <= FormationProfile.Default.CycleDuration;
            elapsed += TimeSpan.FromSeconds(5))
        {
            Collect(line.Advance(elapsed), readings, aliases);
        }

        return readings;
    }

    private static void Collect(
        ImmutableArray<ComposedMessage> messages,
        List<(EquipmentPath, DeviceReading)> readings,
        Dictionary<string, MetricAliasTable> aliases)
    {
        foreach (var message in messages)
        {
            // Node-level messages carry protocol metrics only, and the gateway never forwards those.
            if (message.Topic.DeviceCode is not { } deviceCode)
            {
                continue;
            }

            var path = EquipmentPath.Parse(
                $"NOVAVOLT/NV1/FORMATION/F1/FORM-01/{deviceCode}");

            ImmutableArray<DeviceReading> decoded;

            if (message.Topic.MessageType == SparkplugMessageType.DeviceBirth)
            {
                var birth = SparkplugPayload.DecodeBirth(message.Payload.AsSpan());
                aliases[deviceCode] = birth.Aliases;
                decoded = birth.Readings;
            }
            else
            {
                decoded = SparkplugPayload.DecodeData(
                    message.Payload.AsSpan(),
                    aliases.GetValueOrDefault(deviceCode, MetricAliasTable.Empty));
            }

            foreach (var reading in decoded)
            {
                if (!SparkplugPayload.IsProtocolMetric(reading.MetricName))
                {
                    readings.Add((path, reading));
                }
            }
        }
    }
}
