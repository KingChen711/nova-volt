using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>C06 — compression and retention operate on raw-telemetry chunks.</summary>
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

        // Compression only. Migration 004 scheduled retention at 400 days and migration 007 took it
        // straight back off, because scope.md §8.4 requires a legal hold in front of every retention
        // policy and legal hold is M12. Asserting the absence is the point: a background job that
        // deletes legal records unconditionally must not be able to come back by accident, and the
        // one thing a reviewer cannot see in a diff is a job that is still scheduled.
        policies.ShouldBe([["policy_compression", "7 days", "<null>"]]);

        // EVERY retention policy, which is the word scope.md §8.4 uses. Stated over the whole schema
        // rather than per table because the first version of this fix exempted the rollup on the
        // grounds that it is derived data — and past day 400 it is not derived from anything, it is
        // the last copy. A per-table assertion would have agreed with that mistake; this one is the
        // shape of the rule itself, so the next table to arrive is covered before anyone thinks about
        // it.
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

        // Thirty days between the reading and its write, so both ingests below are counted by the
        // retention-risk counter — this fixture is a small version of the case it exists to measure.
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

        // This is a gateway flush after the chunk was compressed: the physical timestamp is still
        // in that old chunk, while recorded_at is now. Correctness requires accepting it; the lab
        // script records the performance price separately because elapsed-time assertions are flaky.
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
        // The C06 lab, restated after migration 007. It used to run the scheduled retention job; there
        // is no scheduled retention job any more, so it calls drop_chunks by hand — which is now the
        // only way this can happen at all, and that is the substance of the fix rather than a
        // mechanical rewrite. The failure mode itself is unchanged and still real: the reading is
        // recorded NOW, and it is deleted anyway, because the chunk it lands in is chosen by the
        // device clock. Nothing raises, nothing is logged, and the row was a legal record (K4).
        //
        // The retention-risk counter is asserted in the same breath, because a number that fires on
        // this case and stays quiet on ordinary late data is the only thing that lets M12 decide when
        // retention may come back.
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

        // One of the two, not both. The current reading is a day behind its write — a gateway that
        // buffered, which happens constantly — and a counter that fired on that would be a counter
        // nobody reads by the second week.
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

        // Retention acts on the device-time chunk. The claim intentionally remains global, because
        // deleting it would let a replay of the old measurement through as new (ADR-030).
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ts.telemetry_measurement;"))
            .ShouldBe(["1"]);
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT count(*)::text FROM ingest.processed_message;"))
            .ShouldBe(["2"]);
        (await CountChunksContainingAsync(dataSource, expiredAt)).ShouldBe(0);

        // Retention must not erase the dedup authority. Replaying the exact natural key is still a
        // duplicate and cannot recreate telemetry after its raw-data horizon has elapsed.
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

        // Reverse order, so this proves the supported chain: 007 down puts retention back on the
        // schedule and 004 down then has both policies to remove. Running 004 down on its own would
        // pass here for the wrong reason — there is nothing to remove until 007 is undone.
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
