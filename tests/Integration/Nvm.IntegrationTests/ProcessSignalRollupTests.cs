using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>C07 — real process signals roll up by site and by exact UTC minute.</summary>
public sealed class ProcessSignalRollupTests
{
    private static readonly EquipmentPath Nv1Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    private static readonly EquipmentPath De1Channel =
        EquipmentPath.Parse("NOVAVOLT/DE1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    /// <summary>A second channel on the same machine, so the machine rollup has something to weigh.</summary>
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

        // Jobs key their configs with the internal materialization-hypertable id. Join through the
        // continuous-aggregate catalogue rather than assuming its generated name or id is stable.
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

        // Refresh and compression, and NO retention. Migration 005 scheduled a 15-year retention on
        // the rollup and migration 010 took it back off, for the same reason 007 did on raw:
        // scope.md §8.4 puts a legal hold in front of EVERY retention policy, and the hold is M12.
        // The rollup is not the exception it looks like — past day 400 the raw rows are gone and it
        // is the only surviving record of its period, so its retention job was quietly the last
        // deletion in the chain.
        //
        // Compression arrived in migration 011 and is a different kind of job entirely: it changes
        // how rows are stored, never whether they exist. It is here because the rollup was the only
        // relation in the read path with no clustering at all — measured at roughly three useful
        // rows per 8 kB page — which took D2 from 144 to 201 ms once the database grew. The seven
        // day horizon matches the raw table so the newest week, the part an operator refreshes
        // most, stays rowstore.
        //
        // This assertion is exact on purpose. It is what failed when 011 was added, and that is the
        // point: a new job on the rollup must be a decision someone writes down here, not something
        // that appears because a migration happened to call add_*_policy.
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

        // WITH NO DATA is intentional: migration does not hide a potentially unbounded backfill.
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ts.process_signal_1m WHERE site_id = 'NV1';"))
            .ShouldBe(["0"]);

        // The compatibility view exposes only values for which avg/min/max have physical meaning.
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

        // The second site remains independently addressable; every process-data read filters it (K3).
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

        // Reverse order. The site-scoped read views of migration 008 are built ON the rollup, so
        // dropping the rollup first is refused — and that refusal is correct: a rollback able to take
        // a security boundary with it silently is not one anybody should run by accident.
        //
        // 013 first: the machine rollup is a continuous aggregate built ON
        // ts.process_signal_1m, so migration 005 cannot drop the rollup while it still has a
        // dependant. Then 011, because leaving its compression policy in place would hand 005 a
        // rollup with a scheduled job still attached.
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
            // Compression only. Retention was unscheduled by migration 007 and stays unscheduled;
            // rolling the rollup back is not an occasion to quietly re-arm deletion of raw evidence.
            .ShouldBe(["1"]);
    }

    [Fact]
    public async Task TheMachineRollup_WeighsChannelsByTheirSampleCountInsteadOfAveragingAverages()
    {
        // Migration 013, and the whole reason it exists is the number this test pins.
        //
        // "Average temperature of FORM-01 this minute" is a question about a MACHINE. Storing the
        // answer per channel and re-aggregating on every read was costing 1.312,404 ms once the
        // database held the line's other nine cyclers, because a machine-scope read had to scan
        // 8.000 compressed segments to find its 100. The machine rollup answers it directly.
        //
        // The arithmetic here is the trap the migration had to avoid. Two channels in the same
        // minute with DIFFERENT sample counts:
        //
        //     CH-0001   three readings at 10   ->  avg 10, count 3
        //     CH-0002   one reading   at 30    ->  avg 30, count 1
        //
        //     weighted   (10*3 + 30*1) / 4  =  15   <- what a thermometer would have read
        //     unweighted (10 + 30) / 2      =  20   <- what avg(avg_value) returns
        //
        // Both are plausible-looking numbers and only one is the temperature. Report-by-exception
        // guarantees channels do not share a sample count, so the two answers differ in practice
        // rather than in theory, and a test that used equal counts would pass either way.
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

        // A hierarchical continuous aggregate is not refreshed merely because its parent is.
        // Pin the stale-child state that N-M3-8 exposed before refreshing the child explicitly.
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

        // The machine id is the channel's parent path, not a string the caller supplies. A rollup
        // that trusted a caller-provided machine name would let two spellings of one machine become
        // two machines, and nothing downstream would notice.
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
