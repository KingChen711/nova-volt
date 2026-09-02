using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>C06 — compression và retention hoạt động trên raw-telemetry chunk.</summary>
public sealed class TelemetryPolicyTests
{
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    [Fact]
    public async Task Migration_SchedulesCompressionAndDeliberatelyLeavesRetentionUnscheduled()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var policies = await ReadRowsAsync(
            dataSource,
            """
            SELECT proc_name,
                   config ->> 'compress_after' AS compress_after,
                   config ->> 'drop_after' AS drop_after
            FROM timescaledb_information.jobs
            WHERE hypertable_schema = 'ts'
              AND hypertable_name = 'telemetry_measurement'
              AND proc_name IN ('policy_compression', 'policy_retention')
            ORDER BY proc_name;
            """);

        // Chỉ compression. Migration 004 schedule retention 400 ngày rồi migration 007 gỡ ngay, vì
        // scope.md §8.4 yêu cầu legal hold đứng trước mọi retention policy và legal hold là M12.
        // Assertion về sự vắng mặt là điểm chính: background job xóa legal record vô điều kiện không
        // được vô tình trở lại, và thứ reviewer không thấy trong diff là job vẫn đang schedule.
        policies.ShouldBe([["policy_compression", "7 days", "<null>"]]);

        // MỌI retention policy, đúng từ scope.md §8.4 dùng. Phát biểu trên toàn schema thay vì từng
        // table vì phiên bản đầu của fix miễn rollup với lý do nó là derived data — qua ngày 400 nó
        // không derive từ gì nữa, mà là bản sao cuối. Assertion theo table sẽ đồng ý với sai lầm đó;
        // cái này là shape của chính rule, nên table tới sau được cover trước khi ai nghĩ về nó.
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_retention'
              AND (hypertable_schema = 'ts' OR hypertable_schema LIKE '\_timescaledb%');
            """))
            .ShouldBe(["0"]);

        var settings = await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT compression_enabled::text
            FROM timescaledb_information.hypertables
            WHERE hypertable_schema = 'ts' AND hypertable_name = 'telemetry_measurement';
            """);

        settings.ShouldBe(["true"]);

        var compressionShape = await ReadRowsAsync(
            dataSource,
            """
            SELECT attname,
                   coalesce(segmentby_column_index::text, '<null>'),
                   coalesce(orderby_column_index::text, '<null>'),
                   coalesce(orderby_asc::text, '<null>')
            FROM timescaledb_information.compression_settings
            WHERE hypertable_schema = 'ts' AND hypertable_name = 'telemetry_measurement'
            ORDER BY segmentby_column_index NULLS LAST, orderby_column_index NULLS LAST;
            """);

        compressionShape.ShouldBe(
        [
            ["site_id", "1", "<null>", "<null>"],
            ["equipment_id", "2", "<null>", "<null>"],
            ["signal_code", "3", "<null>", "<null>"],
            ["device_timestamp", "<null>", "1", "false"],
        ]);
    }

    [Fact]
    public async Task CompressedChunk_RemainsReadableAndAcceptsALateReading()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var at = new DateTimeOffset(2026, 1, 12, 10, 0, 0, TimeSpan.Zero);
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(at.AddDays(30)),
            new IngestionMetrics());

        // Ba mươi ngày giữa reading và lúc ghi, nên cả hai ingest dưới đây được retention-risk counter
        // đếm — fixture là bản nhỏ của case nó tồn tại để đo.
        (await ingestor.IngestAsync([Message(at, 3.65)], CancellationToken.None))
            .ShouldBe(new IngestionResult(1, 0, RetentionRisk: 1));

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            """
            SELECT compress_chunk(chunk, if_not_compressed => true)
            FROM show_chunks('ts.telemetry_measurement') AS chunk;
            """);

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ts.telemetry_measurement;"))
            .ShouldBe(["1"]);

        // Đây là gateway flush sau khi chunk compressed: physical timestamp vẫn ở chunk cũ còn
        // recorded_at là bây giờ. Correctness yêu cầu chấp nhận nó; lab script ghi performance price
        // riêng vì assertion elapsed-time flaky.
        (await ingestor.IngestAsync([Message(at.AddMinutes(1), 3.66)], CancellationToken.None))
            .ShouldBe(new IngestionResult(1, 0, RetentionRisk: 1));

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text, round(sum(real_value)::numeric, 2)::text FROM ts.telemetry_measurement;"))
            .ShouldBe(["2", "7.31"]);
    }

    [Fact]
    public async Task DroppingChunks_WouldDeleteAFreshRecordWhoseDeviceClockIsFiveHundredDaysOld()
    {
        // Lab C06, phát biểu lại sau migration 007. Nó từng chạy scheduled retention job; giờ không còn
        // job đó nên gọi drop_chunks bằng tay — cách duy nhất điều này có thể xảy ra, và đó là bản chất
        // của fix chứ không phải mechanical rewrite. Failure mode không đổi và vẫn thật: reading được
        // recorded NGAY BÂY GIỜ nhưng vẫn bị xóa vì chunk nó vào được chọn bởi device clock. Không gì
        // raise, không gì log, và row là legal record (K4).
        //
        // Retention-risk counter được assert cùng lúc, vì number fire ở case này nhưng im lặng với late
        // data thông thường là thứ duy nhất để M12 quyết định retention có thể trở lại khi nào.
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var databaseNow = DateTimeOffset.Parse(
            (await TelemetryHypertableTests.ReadAsync(dataSource, "SELECT now()::text;"))[0],
            System.Globalization.CultureInfo.InvariantCulture);
        var metrics = new IngestionMetrics();
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(databaseNow),
            metrics);

        var expiredAt = databaseNow.AddDays(-500);
        var expired = Message(expiredAt, 3.60, gatewayTimestamp: databaseNow);
        var current = Message(databaseNow.AddDays(-1), 3.70);
        (await ingestor.IngestAsync([expired, current], CancellationToken.None))
            .ShouldBe(new IngestionResult(2, 0, Drifted: 1, RetentionRisk: 1));

        // Một trong hai, không phải cả hai. Reading hiện tại lùi một ngày sau lúc ghi — gateway buffer,
        // chuyện xảy ra thường xuyên — và counter fire vì vậy sẽ là counter không ai đọc sau tuần hai.
        metrics.RetentionRiskCount.ShouldBe(1);
        (await CountChunksContainingAsync(dataSource, expiredAt)).ShouldBe(1);

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ts.telemetry_measurement;"))
            .ShouldBe(["2"]);

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM timescaledb_information.jobs
            WHERE hypertable_schema = 'ts'
              AND hypertable_name = 'telemetry_measurement'
              AND proc_name = 'policy_retention';
            """))
            .ShouldBe(["0"]);

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            "SELECT drop_chunks('ts.telemetry_measurement', older_than => INTERVAL '400 days');");

        // Retention tác động trên chunk device-time. Claim cố ý vẫn global, vì xóa nó sẽ để replay
        // measurement cũ đi qua như mới (ADR-030).
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ts.telemetry_measurement;"))
            .ShouldBe(["1"]);
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ingest.processed_message;"))
            .ShouldBe(["2"]);
        (await CountChunksContainingAsync(dataSource, expiredAt)).ShouldBe(0);

        // Retention không được xóa dedup authority. Replay đúng natural key vẫn là duplicate và không
        // thể tạo lại telemetry sau khi raw-data horizon đã qua.
        (await ingestor.IngestAsync([expired], CancellationToken.None))
            .ShouldBe(new IngestionResult(0, 1));
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ts.telemetry_measurement;"))
            .ShouldBe(["1"]);
        (await CountChunksContainingAsync(dataSource, expiredAt)).ShouldBe(0);
    }

    [Fact]
    public async Task DownScript_RemovesBothPoliciesAndMakesEveryChunkWritableRowstoreAgain()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var at = new DateTimeOffset(2026, 1, 12, 10, 0, 0, TimeSpan.Zero);
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(at.AddDays(30)),
            new IngestionMetrics());
        await ingestor.IngestAsync([Message(at, 3.65)], CancellationToken.None);
        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            "SELECT compress_chunk(chunk) FROM show_chunks('ts.telemetry_measurement') AS chunk;");

        // Thứ tự ngược để chứng minh supported chain: 007 down đưa retention lại schedule và 004 down
        // có cả hai policy để gỡ. Chạy riêng 004 down sẽ pass ở đây vì lý do sai — không có gì để gỡ
        // cho đến khi undo 007.
        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("007_retention_awaits_legal_hold"),
                CancellationToken.None));

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT config ->> 'drop_after'
            FROM timescaledb_information.jobs
            WHERE hypertable_schema = 'ts'
              AND hypertable_name = 'telemetry_measurement'
              AND proc_name = 'policy_retention';
            """))
            .ShouldBe(["400 days"]);

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("004_telemetry_policies"),
                CancellationToken.None));

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM timescaledb_information.jobs
            WHERE hypertable_schema = 'ts'
              AND hypertable_name = 'telemetry_measurement'
              AND proc_name IN ('policy_compression', 'policy_retention');
            """))
            .ShouldBe(["0"]);

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT compression_enabled::text
            FROM timescaledb_information.hypertables
            WHERE hypertable_schema = 'ts' AND hypertable_name = 'telemetry_measurement';
            """))
            .ShouldBe(["false"]);
    }

    private static DecodedSparkplugMessage Message(
        DateTimeOffset deviceTimestamp,
        double value,
        DateTimeOffset? gatewayTimestamp = null)
    {
        var topic = SparkplugTopic.For(Channel, SparkplugMessageType.DeviceData);
        var reading = new DeviceReading("Formation/Voltage", Alias: 1, new MetricValue.Real(value), deviceTimestamp);

        return new DecodedSparkplugMessage(
            "NV1",
            Channel,
            topic,
            gatewayTimestamp ?? deviceTimestamp,
            [reading]);
    }

    private static async Task<long> CountChunksContainingAsync(
        NpgsqlDataSource dataSource,
        DateTimeOffset timestamp)
    {
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM timescaledb_information.chunks
            WHERE hypertable_schema = 'ts'
              AND hypertable_name = 'telemetry_measurement'
              AND @timestamp >= range_start
              AND @timestamp < range_end;
            """,
            connection);
        command.Parameters.AddWithValue("timestamp", timestamp);

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("TimescaleDB did not return the chunk count."));
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "The only caller passes a catalogue query literal in this test file; no value comes "
            + "from outside the test assembly.")]
    private static async Task<string[][]> ReadRowsAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var rows = new List<string[]>();

        while (await reader.ReadAsync(CancellationToken.None))
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = await reader.IsDBNullAsync(index, CancellationToken.None)
                    ? "<null>"
                    : reader.GetValue(index).ToString() ?? string.Empty;
            }

            rows.Add(values);
        }

        return [.. rows];
    }
}
