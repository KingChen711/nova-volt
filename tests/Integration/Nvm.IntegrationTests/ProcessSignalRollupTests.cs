using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>C07 — process signal thật được rollup theo site và đúng từng phút UTC.</summary>
public sealed class ProcessSignalRollupTests
{
    private static readonly EquipmentPath Nv1Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    private static readonly EquipmentPath De1Channel =
        EquipmentPath.Parse("NOVAVOLT/DE1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    /// <summary>Channel thứ hai trên cùng máy, để machine rollup có dữ liệu để weigh.</summary>
    private static readonly EquipmentPath Nv1SecondChannel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0002");

    [Fact]
    public async Task Migration_CreatesAMaterializedOnlyRollupWithBothLifecyclePolicies()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var aggregate = await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT hypertable_schema,
                   hypertable_name,
                   materialized_only::text,
                   materialization_hypertable_schema,
                   (materialization_hypertable_name IS NOT NULL)::text
            FROM timescaledb_information.continuous_aggregates
            WHERE view_schema = 'ts' AND view_name = 'process_signal_1m';
            """);

        aggregate.ShouldBe(["ts", "telemetry_measurement", "true", "_timescaledb_internal", "true"]);

        // Job key config bằng id materialization-hypertable nội bộ. Join qua catalogue continuous
        // aggregate thay vì giả định name hoặc id được generate của nó ổn định.
        var policies = await ReadRowsAsync(
            dataSource,
            """
            WITH aggregate AS (
                SELECT materialization_hypertable_schema AS schema_name,
                       materialization_hypertable_name AS table_name
                FROM timescaledb_information.continuous_aggregates
                WHERE view_schema = 'ts' AND view_name = 'process_signal_1m'
            ), materialization AS (
                SELECT hypertable.id
                FROM _timescaledb_catalog.hypertable AS hypertable
                JOIN aggregate
                  ON aggregate.schema_name = hypertable.schema_name
                 AND aggregate.table_name = hypertable.table_name
            )
            SELECT jobs.proc_name,
                   jobs.schedule_interval::text,
                   coalesce(jobs.config ->> 'start_offset', '<null>'),
                   coalesce(jobs.config ->> 'end_offset', '<null>'),
                   coalesce(jobs.config ->> 'drop_after', '<null>'),
                   coalesce(jobs.config ->> 'compress_after', '<null>')
            FROM timescaledb_information.jobs AS jobs
            CROSS JOIN materialization
            WHERE nullif(jobs.config ->> 'mat_hypertable_id', '')::INTEGER = materialization.id
               OR nullif(jobs.config ->> 'hypertable_id', '')::INTEGER = materialization.id
            ORDER BY jobs.proc_name;
            """);

        // Refresh và compression, KHÔNG retention. Migration 005 schedule retention 15 năm trên rollup
        // và migration 010 gỡ nó, cùng lý do 007 gỡ ở raw: scope.md §8.4 đặt legal hold trước MỌI
        // retention policy, và hold là M12. Rollup không phải ngoại lệ như vẻ ngoài — qua ngày 400, raw
        // row đã mất và nó là record duy nhất còn lại của period, nên retention job của nó âm thầm là
        // lần xóa cuối chuỗi.
        //
        // Compression đến ở migration 011 và là loại job hoàn toàn khác: nó đổi cách lưu row, không
        // bao giờ đổi row có tồn tại hay không. Nó ở đây vì rollup là relation duy nhất trên read path
        // hoàn toàn không có clustering — đo được khoảng ba row hữu ích mỗi page 8 kB — khiến D2 từ 144
        // thành 201 ms khi database lớn lên. Chân trời bảy ngày khớp raw table, nên tuần mới nhất,
        // phần operator refresh nhiều nhất, vẫn là rowstore.
        //
        // Assertion này chính xác có chủ ý. Đây là thứ fail khi thêm 011, và đó là điểm chính: job mới
        // trên rollup phải là quyết định ai đó ghi ở đây, không phải thứ xuất hiện vì migration tình cờ
        // gọi add_*_policy.
        policies.ShouldBe(
        [
            ["policy_compression", "12:00:00", "<null>", "<null>", "<null>", "7 days"],
            ["policy_refresh_continuous_aggregate", "00:01:00", "05:00:00", "00:01:00", "<null>", "<null>"],
        ]);
    }

    [Fact]
    public async Task Refresh_CountsEveryRealSampleOnceAtTheMinuteBoundaryAndSeparatesSites()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var start = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(start.AddMinutes(3)),
            new IngestionMetrics());

        var nv1 = Message(
            "NV1",
            Nv1Channel,
            start.AddMinutes(2),
            new DeviceReading("Formation/Temperature", 3, new MetricValue.Real(20), start),
            new DeviceReading("Formation/Temperature", 3, new MetricValue.Real(22), start.AddSeconds(59).AddMilliseconds(999)),
            new DeviceReading("Formation/Temperature", 3, new MetricValue.Real(30), start.AddMinutes(1)),
            new DeviceReading("Formation/StepIndex", 4, new MetricValue.Integral(7), start.AddSeconds(30)));
        var de1 = Message(
            "DE1",
            De1Channel,
            start.AddMinutes(2),
            new DeviceReading("Formation/Temperature", 3, new MetricValue.Real(99), start));

        (await ingestor.IngestAsync([nv1, de1], CancellationToken.None))
            .ShouldBe(new IngestionResult(5, 0));

        // WITH NO DATA có chủ ý: migration không che giấu một backfill có thể không bị giới hạn.
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ts.process_signal_1m WHERE site_id = 'NV1';"))
            .ShouldBe(["0"]);

        // Compatibility view chỉ expose value mà avg/min/max có ý nghĩa vật lý.
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text, min(value)::text, max(value)::text
            FROM ts.process_signal
            WHERE site_id = 'NV1'
              AND equipment_id = 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001';
            """))
            .ShouldBe(["3", "20", "30"]);

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            """
            CALL refresh_continuous_aggregate(
                'ts.process_signal_1m',
                TIMESTAMPTZ '2026-01-15 10:00:00+00',
                TIMESTAMPTZ '2026-01-15 10:02:00+00',
                force => false);
            """);

        var nv1Buckets = await ReadRowsAsync(
            dataSource,
            """
            SELECT to_char(bucket, 'YYYY-MM-DD HH24:MI:SSOF'),
                   round(avg_value::numeric, 3)::text,
                   min_value::text,
                   max_value::text,
                   sample_count::text
            FROM ts.process_signal_1m
            WHERE site_id = 'NV1'
              AND equipment_id = 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001'
              AND signal_code = 'Formation/Temperature'
            ORDER BY bucket;
            """);

        nv1Buckets.ShouldBe(
        [
            ["2026-01-15 10:00:00+00", "21.000", "20", "22", "2"],
            ["2026-01-15 10:01:00+00", "30.000", "30", "30", "1"],
        ]);

        // Site thứ hai vẫn address được độc lập; mọi lần đọc process data đều filter nó (K3).
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text, sum(sample_count)::text, max(max_value)::text
            FROM ts.process_signal_1m
            WHERE site_id = 'DE1'
              AND equipment_id = 'NOVAVOLT/DE1/FORMATION/F1/FORM-01/FORM-01-CH-0001'
              AND signal_code = 'Formation/Temperature';
            """))
            .ShouldBe(["1", "1", "99"]);
    }

    [Fact]
    public async Task DownScript_RemovesOnlyTheRollupAndLeavesRawTelemetryPoliciesIntact()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());

        // Thứ tự ngược. Site-scoped read view của migration 008 được build TRÊN rollup, nên drop rollup
        // trước bị từ chối — và từ chối đó đúng: rollback có thể âm thầm mang theo security boundary
        // không phải thứ ai nên vô tình chạy.
        //
        // 013 trước: machine rollup là continuous aggregate build TRÊN ts.process_signal_1m, nên
        // migration 005 không thể drop rollup khi nó còn dependant. Rồi 011, vì để compression policy
        // của nó tại chỗ sẽ đưa cho 005 rollup vẫn gắn scheduled job.
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM pg_views
            WHERE schemaname = 'ts_scoped'
              AND viewname = 'process_signal_machine_1m';
            """)).ShouldBe(["1"]);

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("014_scoped_machine_rollup"),
                CancellationToken.None));

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM pg_views
            WHERE schemaname = 'ts_scoped'
              AND viewname = 'process_signal_machine_1m';
            """)).ShouldBe(["0"]);

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("013_machine_level_rollup"),
                CancellationToken.None));
        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("011_rollup_compression_for_read_locality"),
                CancellationToken.None));
        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("010_rollup_retention_awaits_legal_hold"),
                CancellationToken.None));
        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("008_site_scoped_read_access"),
                CancellationToken.None));
        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("005_process_signal_rollup"),
                CancellationToken.None));

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT to_regclass('ts.process_signal')::text, to_regclass('ts.process_signal_1m')::text;"))
            .ShouldBe(["<null>", "<null>"]);

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM timescaledb_information.jobs
            WHERE hypertable_schema = 'ts' AND hypertable_name = 'process_signal_1m';
            """))
            .ShouldBe(["0"]);

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM timescaledb_information.jobs
            WHERE hypertable_schema = 'ts'
              AND hypertable_name = 'telemetry_measurement'
              AND proc_name IN ('policy_compression', 'policy_retention');
            """))
            // Chỉ compression. Retention đã được migration 007 unschedule và vẫn vậy; rollback rollup
            // không phải dịp âm thầm re-arm việc xóa raw evidence.
            .ShouldBe(["1"]);
    }

    [Fact]
    public async Task TheMachineRollup_WeighsChannelsByTheirSampleCountInsteadOfAveragingAverages()
    {
        // Migration 013, và toàn bộ lý do nó tồn tại là con số test này pin.
        //
        // "Nhiệt độ trung bình của FORM-01 trong phút này" là câu hỏi về MÁY. Lưu câu trả lời theo
        // channel và re-aggregate mỗi lần đọc tốn 1.312,404 ms khi database đã có chín cycler khác của
        // line, vì lần đọc scope máy phải scan 8.000 compressed segment để tìm 100 segment của nó.
        // Machine rollup trả lời trực tiếp.
        //
        // Phép tính ở đây là bẫy migration phải tránh. Hai channel trong cùng phút có sample count
        // KHÁC NHAU:
        //
        //     CH-0001   ba reading ở 10        ->  avg 10, count 3
        //     CH-0002   một reading ở 30       ->  avg 30, count 1
        //
        //     weighted   (10*3 + 30*1) / 4  =  15   <- điều thermocouple đã đọc
        //     unweighted (10 + 30) / 2      =  20   <- điều avg(avg_value) trả về
        //
        // Cả hai đều là số nghe có vẻ hợp lý nhưng chỉ một là nhiệt độ. Report-by-exception đảm bảo
        // channel không cùng sample count, nên hai đáp án khác nhau trên thực tế chứ không chỉ lý
        // thuyết; test dùng count bằng nhau sẽ pass cả hai cách.
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var start = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(start.AddMinutes(3)),
            new IngestionMetrics());

        var busy = Message(
            "NV1",
            Nv1Channel,
            start.AddMinutes(2),
            new DeviceReading("Formation/Temperature", 3, new MetricValue.Real(10), start),
            new DeviceReading("Formation/Temperature", 3, new MetricValue.Real(10), start.AddSeconds(20)),
            new DeviceReading("Formation/Temperature", 3, new MetricValue.Real(10), start.AddSeconds(40)));
        var quiet = Message(
            "NV1",
            Nv1SecondChannel,
            start.AddMinutes(2),
            new DeviceReading("Formation/Temperature", 3, new MetricValue.Real(30), start.AddSeconds(10)));

        (await ingestor.IngestAsync([busy, quiet], CancellationToken.None))
            .ShouldBe(new IngestionResult(4, 0));

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            """
            CALL refresh_continuous_aggregate(
                'ts.process_signal_1m',
                TIMESTAMPTZ '2026-01-15 10:00:00+00',
                TIMESTAMPTZ '2026-01-15 10:01:00+00',
                force => false);
            """);

        // Continuous aggregate phân cấp không tự refresh chỉ vì parent đã refresh. Pin trạng thái
        // child stale mà N-M3-8 lộ ra trước khi explicit refresh child.
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT
                (SELECT coalesce(sum(sample_count), 0)::text
                 FROM ts.process_signal_1m
                 WHERE site_id = 'NV1'
                   AND equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/%'
                   AND signal_code = 'Formation/Temperature'),
                (SELECT coalesce(sum(sample_count), 0)::text
                 FROM ts.process_signal_machine_1m
                 WHERE site_id = 'NV1'
                   AND machine_id = 'NOVAVOLT/NV1/FORMATION/F1/FORM-01'
                   AND signal_code = 'Formation/Temperature');
            """)).ShouldBe(["4", "0"]);

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            """
            CALL refresh_continuous_aggregate(
                'ts.process_signal_machine_1m',
                TIMESTAMPTZ '2026-01-15 10:00:00+00',
                TIMESTAMPTZ '2026-01-15 10:01:00+00',
                force => false);
            """);

        (await ReadRowsAsync(
            dataSource,
            """
            SELECT machine_id,
                   round(avg_value::numeric, 3)::text,
                   min_value::text,
                   max_value::text,
                   sample_count::text
            FROM ts.process_signal_machine_1m
            WHERE site_id = 'NV1' AND signal_code = 'Formation/Temperature'
            ORDER BY bucket;
            """))
            .ShouldBe(
            [
                ["NOVAVOLT/NV1/FORMATION/F1/FORM-01", "15.000", "10", "30", "4"],
            ]);

        // Machine id là parent path của channel, không phải string caller cung cấp. Rollup tin machine
        // name do caller cấp sẽ để hai cách viết của một máy thành hai máy, mà downstream không nhận ra.
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(DISTINCT machine_id)::text
            FROM ts.process_signal_machine_1m
            WHERE site_id = 'NV1';
            """))
            .ShouldBe(["1"]);
    }

    private static DecodedSparkplugMessage Message(
        string siteId,
        EquipmentPath channel,
        DateTimeOffset gatewayTimestamp,
        params DeviceReading[] readings) =>
        new(
            siteId,
            channel,
            SparkplugTopic.For(channel, SparkplugMessageType.DeviceData),
            gatewayTimestamp,
            [.. readings]);

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "Every caller passes a literal from this test file. The SQL interrogates the catalogue "
            + "or reads a site-filtered rollup; no value comes from outside the test assembly.")]
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
