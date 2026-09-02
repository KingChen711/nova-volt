using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Simulator.Formation;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>
/// Lab phá hoại #2 của scope.md §9/M2, viết thành phép đo có thể lặp lại thay vì one-off.
/// </summary>
/// <remarks>
/// <para>
/// Bài học không phải là bỏ <c>device_timestamp</c> khỏi natural key sẽ làm hỏng thứ gì đó. Nó làm hỏng
/// thứ gì đó <b>âm thầm</b>: deduplication báo thành công, mọi insert trả về không error, và triệu
/// chứng duy nhất là table có ít row hơn số nhà máy đã đo. Đây là loại defect mà test suite xanh không
/// nói lên điều gì nếu không có count.
/// </para>
/// <para>
/// Đo trên output simulator thật thay vì key synthetic, vì data shape là toàn bộ câu hỏi: bao nhiêu
/// reading cùng equipment path và signal code, chỉ khác thời điểm được đo.
/// </para>
/// </remarks>
public sealed class NaturalKeyWithoutDeviceTimestampTests
{
    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 29, 7, 0, 0, TimeSpan.Zero);

    // Instant cố định đại diện cho "key không mang device timestamp". Giá trị không quan trọng; điều
    // quan trọng là mọi reading cùng dùng nó.
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

        // Mọi measurement giữ identity riêng khi instant là một phần của key.
        withTimestamp.Count.ShouldBe(readings.Count);

        // Thiếu nó, identity co lại thành (channel, signal) — một row mỗi channel mỗi signal, cho cả
        // cycle mười tám giờ. Formation curve thành một point duy nhất.
        var swallowed = readings.Count - withoutTimestamp.Count;

        // In ra, không chỉ assert. Đây là lab: con số là kết quả, và benchmarks.md cần nguyên văn nó
        // thay vì suy ra lại từ inequality.
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

        // Sample năm giây trong cả cycle: sample period mà nhà máy thực sự chạy, nên ratio đo được là
        // ratio của nhà máy chứ không phải tỷ lệ được chọn để minh họa.
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
            // Message node-level chỉ mang protocol metric, và gateway không bao giờ forward chúng.
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
