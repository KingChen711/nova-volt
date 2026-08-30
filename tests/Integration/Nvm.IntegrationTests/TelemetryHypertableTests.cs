using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>C05 — telemetry is a hypertable on device time, and nothing M2 promised broke.</summary>
public sealed class TelemetryHypertableTests
{
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    [Fact]
    public async Task Migration_TurnsTelemetryIntoAHypertableWithoutBreakingTheM2Contract()
    {
        await using var postgres = await StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());

        // 1. It is a hypertable, and it is partitioned on the clock ADR-011 chose.
        var dimension = await ReadAsync(
            dataSource,
            """
            SELECT d.column_name, d.time_interval::text
            FROM timescaledb_information.dimensions d
            WHERE d.hypertable_schema = 'ts' AND d.hypertable_name = 'telemetry_measurement';
            """);

        dimension.ShouldBe(["device_timestamp", "1 day"]);

        // 2. The primary key had to be restated to contain the partitioning column, and it did not
        //    quietly become something weaker: it is still a primary key, and it still starts with the
        //    id the claim table keys on.
        var primaryKey = await ReadAsync(
            dataSource,
            """
            SELECT string_agg(a.attname, ',' ORDER BY k.ordinality)
            FROM pg_constraint c
            CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ordinality)
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
            WHERE c.conrelid = 'ts.telemetry_measurement'::regclass AND c.contype = 'p';
            """);

        primaryKey.ShouldBe(["source_event_id,device_timestamp"]);

        // 3. The strongest statement of M2 survives conversion: no telemetry row without a claim.
        //    TimescaleDB accepting a foreign key from a hypertable to an ordinary table is the thing
        //    plan §C05 said to check rather than assume — it does, on 2.29.2-pg17.
        var foreignKey = await ReadAsync(
            dataSource,
            """
            SELECT confrelid::regclass::text
            FROM pg_constraint
            WHERE conrelid = 'ts.telemetry_measurement'::regclass AND contype = 'f';
            """);

        foreignKey.ShouldBe(["ingest.processed_message"]);

        // 4. And the write path still works end to end, landing rows in chunks rather than in a heap.
        var metrics = new IngestionMetrics();
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)),
            metrics);

        // Three days apart on the device clock, so they cannot land in one chunk.
        var messages = new[]
        {
            Message(new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero)),
            Message(new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero)),
            Message(new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero)),
        };

        // Two of the three sit more than a chunk behind the moment they are written, which is the
        // whole point of the fixture: they land in older chunks. That is also exactly what the
        // retention-risk counter counts, so it reports two here rather than nothing.
        (await ingestor.IngestAsync(messages, CancellationToken.None))
            .ShouldBe(new IngestionResult(3, 0, RetentionRisk: 2));

        var chunks = await ReadAsync(
            dataSource,
            """
            SELECT count(*)::text FROM timescaledb_information.chunks
            WHERE hypertable_schema = 'ts' AND hypertable_name = 'telemetry_measurement';
            """);

        chunks.ShouldBe(["3"]);
    }

    [Fact]
    public async Task TheDownScript_TakesTheTableBackToAnOrdinaryOneWithItsRowsIntact()
    {
        // scope.md §8.4 requires every migration to have a way back, and a way back that has never
        // been run is a file, not a rollback. This one is more than a DROP: there is no call that
        // un-hypertables a table, so it copies and swaps — and the cost of that is worth knowing
        // before the table has a hundred million rows, not after.
        await using var postgres = await StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)),
            new IngestionMetrics());

        await ingestor.IngestAsync(
            [Message(new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero))],
            CancellationToken.None);

        (await ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM pg_views
            WHERE schemaname = 'ts_scoped'
              AND viewname = 'process_signal_machine_1m';
            """)).ShouldBe(["1"]);

        // Upgrade applies every later migration too. Roll back in reverse order so this test proves
        // the supported chain rather than relying on DROP TABLE to clean up policy side effects.
        //
        // The chain is listed by hand, so every migration that adds a DEPENDANT has to be added
        // here as well. Migration 013 builds a second continuous aggregate on top of
        // ts.process_signal_1m, and leaving it out made this test fail with
        // "cannot drop view ts.process_signal_1m because other objects depend on it" — the test
        // doing its job. 012 sits on the archive table and has no ordering constraint with these,
        // but it goes first for the same reason: newest down first, always.
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                DownScriptPath("014_scoped_machine_rollup"),
                CancellationToken.None));

        (await ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM pg_views
            WHERE schemaname = 'ts_scoped'
              AND viewname = 'process_signal_machine_1m';
            """)).ShouldBe(["0"]);

        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                DownScriptPath("013_machine_level_rollup"),
                CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                DownScriptPath("012_correction_trail_stays_inside_one_site"),
                CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                DownScriptPath("011_rollup_compression_for_read_locality"),
                CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                DownScriptPath("010_rollup_retention_awaits_legal_hold"),
                CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                DownScriptPath("009_raw_curve_correction_trail"),
                CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                DownScriptPath("008_site_scoped_read_access"),
                CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                DownScriptPath("007_retention_awaits_legal_hold"),
                CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(DownScriptPath("006_raw_curve_archive"), CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(DownScriptPath("005_process_signal_rollup"), CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(DownScriptPath("004_telemetry_policies"), CancellationToken.None));
        await ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(DownScriptPath("003_telemetry_hypertable"), CancellationToken.None));

        (await ReadAsync(
            dataSource,
            """
            SELECT count(*)::text FROM timescaledb_information.hypertables
            WHERE hypertable_schema = 'ts' AND hypertable_name = 'telemetry_measurement';
            """)).ShouldBe(["0"]);

        (await ReadAsync(dataSource, "SELECT count(*)::text FROM ts.telemetry_measurement;")).ShouldBe(["1"]);

        // The rollback also has to put the M2 shape back, or the forward migration cannot be run
        // again: it names the constraint it drops.
        (await ReadAsync(
            dataSource,
            """
            SELECT string_agg(a.attname, ',' ORDER BY k.ordinality)
            FROM pg_constraint c
            CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ordinality)
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
            WHERE c.conrelid = 'ts.telemetry_measurement'::regclass AND c.contype = 'p';
            """)).ShouldBe(["source_event_id"]);

        (await ReadAsync(
            dataSource,
            """
            SELECT confrelid::regclass::text FROM pg_constraint
            WHERE conrelid = 'ts.telemetry_measurement'::regclass AND contype = 'f';
            """)).ShouldBe(["ingest.processed_message"]);
    }

    [Fact]
    public async Task WritersForTheSameMissingSlice_PrecreateTheChunkBeforeClaiming()
    {
        // The regression C05 introduced and N-M3-10 reopened. Each writer used to claim first and
        // then race to create the chunk. TimescaleDB copies the telemetry foreign key while creating
        // that chunk, so its ShareRowExclusive request on the claim table formed a cycle with the
        // RowExclusive locks every writer already held there.
        //
        // The fixed path pre-creates the one missing slice before any claim transaction starts. The
        // batch must land exactly once and chunk creation must require zero write-transaction retry;
        // accepting a non-zero count here would put the old lock order back behind a retry loop.
        const int Readings = 4_000;

        await using var postgres = await StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var metrics = new IngestionMetrics();
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero)),
            metrics,
            writerParallelism: 4,
            minRowsPerWriter: 256);

        // One message, four thousand readings a millisecond apart: one day, therefore one chunk, and
        // that chunk does not exist yet.
        var at = new DateTimeOffset(2026, 8, 29, 7, 0, 0, TimeSpan.Zero);
        var readings = ImmutableArray.CreateBuilder<DeviceReading>(Readings);

        for (var index = 0; index < Readings; index++)
        {
            readings.Add(new DeviceReading(
                "Formation/Voltage", Alias: 1, new MetricValue.Real(3.7), at.AddMilliseconds(index)));
        }

        var batch = new DecodedSparkplugMessage(
            "NV1",
            Channel,
            SparkplugTopic.For(Channel, SparkplugMessageType.DeviceData),
            at.AddMinutes(1),
            readings.DrainToImmutable());

        (await ingestor.IngestAsync([batch], CancellationToken.None))
            .ShouldBe(new IngestionResult(Readings, 0));

        (await ReadAsync(dataSource, "SELECT count(*)::text FROM ts.telemetry_measurement;"))
            .ShouldBe([Readings.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        metrics.WriteRetryCount.ShouldBe(0);
    }

    internal static async Task<PostgreSqlContainer> StartAsync()
    {
        var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
            .WithDatabase("novavolt_integration")
            .WithUsername("nvm")
            .WithPassword("nvm_integration_only")
            .Build();

        await postgres.StartAsync(CancellationToken.None);

        // The compose stack creates the extension from deploy/postgres/init; a bare container has not
        // run that, and every migration from 003 onwards needs it.
        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ExecuteAsync(dataSource, "CREATE EXTENSION IF NOT EXISTS timescaledb;");

        return postgres;
    }

    /// <summary>The rollback scripts, which travel to the output directory as content.</summary>
    internal static string DownScriptPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Migrations", "Down", name + ".sql");

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "Every caller passes a literal written in this file. These tests interrogate the catalogue "
            + "and run the checked-in rollback script, neither of which can be expressed with parameters; "
            + "no value here comes from outside the assembly.")]
    internal static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Runs a query and returns the first row as strings, for shape assertions.</summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "Every caller passes a literal written in this file. These tests interrogate the catalogue "
            + "and run the checked-in rollback script, neither of which can be expressed with parameters; "
            + "no value here comes from outside the assembly.")]
    internal static async Task<string[]> ReadAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        (await reader.ReadAsync(CancellationToken.None)).ShouldBeTrue();

        var values = new string[reader.FieldCount];

        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = await reader.IsDBNullAsync(index, CancellationToken.None) ? "<null>" : reader.GetValue(index).ToString() ?? string.Empty;
        }

        return values;
    }

    private static DecodedSparkplugMessage Message(DateTimeOffset deviceTimestamp)
    {
        var topic = SparkplugTopic.For(Channel, SparkplugMessageType.DeviceData);
        var reading = new DeviceReading("Formation/Voltage", Alias: 1, new MetricValue.Real(3.7), deviceTimestamp);

        return new DecodedSparkplugMessage(
            "NV1",
            Channel,
            topic,
            deviceTimestamp.AddSeconds(10),
            ImmutableArray.Create(reading));
    }
}
