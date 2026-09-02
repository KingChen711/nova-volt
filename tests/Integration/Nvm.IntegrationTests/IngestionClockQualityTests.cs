using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// D5, và quy tắc ba timestamp đứng sau nó. Điều được kiểm chứng ở đây không phải là một reading bị
/// drift có được phát hiện hay không — mà là một reading bị drift vẫn được <b>lưu lại</b>. Từ chối
/// nó sẽ để một viên pin CMOS chết âm thầm xóa mất traceability record của một line, cho tới khi
/// một auditor hỏi tới.
/// </summary>
public sealed class IngestionClockQualityTests
{
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");
    private static readonly DateTimeOffset RecordedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DriftedReadingsAreStoredAndFlagged_AndAllThreeTimestampsSurvive()
    {
        await using var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
            .WithDatabase("novavolt_integration")
            .WithUsername("nvm")
            .WithPassword("nvm_integration_only")
            .Build();

        await postgres.StartAsync(CancellationToken.None);
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var clock = new FakeTimeProvider(RecordedAt);
        var metrics = new IngestionMetrics();
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            clock,
            metrics,
            clockDriftThreshold: ClockQualityClassifier.DefaultThreshold);

        // Gateway nhận cả hai cùng một thời điểm. Một thiết bị chậm hai giờ; thiết bị còn lại lệch
        // mười giây, vốn là một đồng hồ khỏe mạnh bình thường.
        var gatewayTimestamp = RecordedAt.AddSeconds(-30);
        var drifted = Message("Formation/Voltage", gatewayTimestamp.AddHours(-2), gatewayTimestamp);
        var healthy = Message("Formation/Current", gatewayTimestamp.AddSeconds(-10), gatewayTimestamp);

        var result = await ingestor.IngestAsync([drifted, healthy], TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(2);
        result.Drifted.ShouldBe(1);
        metrics.DriftedCount.ShouldBe(1);

        var rows = await ReadAsync(dataSource, TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(2);

        // D5: có mặt, và nói rằng nó không thể được tin tưởng.
        var flagged = rows["Formation/Voltage"];
        flagged.ClockQuality.ShouldBe("Drifted");
        flagged.DeviceTimestamp.ShouldBe(gatewayTimestamp.AddHours(-2));

        rows["Formation/Current"].ClockQuality.ShouldBe("Good");

        // scope.md §7.3: ba timestamp, ba câu hỏi khác nhau, ba giá trị khác nhau. M3 sắp xếp theo
        // device_timestamp cho business và theo recorded_at cho audit, và một cột bị ghi đè bởi cột
        // khác sẽ âm thầm gộp hai cách sắp xếp đó thành một.
        foreach (var row in rows.Values)
        {
            row.DeviceTimestamp.ShouldNotBe(row.GatewayTimestamp);
            row.GatewayTimestamp.ShouldNotBe(row.RecordedAt);
            row.DeviceTimestamp.ShouldNotBe(row.RecordedAt);
            row.GatewayTimestamp.ShouldBe(gatewayTimestamp);
            row.RecordedAt.ShouldBe(RecordedAt);
        }
    }

    private static DecodedSparkplugMessage Message(
        string signalCode,
        DateTimeOffset deviceTimestamp,
        DateTimeOffset gatewayTimestamp) =>
        new(
            "NV1",
            Channel,
            SparkplugTopic.For(Channel, SparkplugMessageType.DeviceData),
            gatewayTimestamp,
            ImmutableArray.Create(
                new DeviceReading(signalCode, Alias: 1, new MetricValue.Real(3.7), deviceTimestamp)));

    private static async Task<Dictionary<string, StoredRow>> ReadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT signal_code, clock_quality, device_timestamp, gateway_timestamp, recorded_at
            FROM ts.telemetry_measurement;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new Dictionary<string, StoredRow>(StringComparer.Ordinal);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetString(0)] = new StoredRow(
                reader.GetString(1),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken),
                await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken),
                await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken));
        }

        return rows;
    }

    private sealed record StoredRow(
        string ClockQuality,
        DateTimeOffset DeviceTimestamp,
        DateTimeOffset GatewayTimestamp,
        DateTimeOffset RecordedAt);
}
