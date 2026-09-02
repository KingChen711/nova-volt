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

/// <summary>C05 — telemetry là hypertable theo device time, và không lời hứa nào của M2 bị phá vỡ.</summary>
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

        // 1. Đây là hypertable, partition theo clock mà ADR-011 chọn.
        var dimension = await ReadAsync(
            dataSource,
            """
            SELECT d.column_name, d.time_interval::text
            FROM timescaledb_information.dimensions d
            WHERE d.hypertable_schema = 'ts' AND d.hypertable_name = 'telemetry_measurement';
            """);

        dimension.ShouldBe(["device_timestamp", "1 day"]);

        // 2. Primary key phải được phát biểu lại để chứa partitioning column, nhưng không âm thầm trở
        //    nên yếu hơn: nó vẫn là primary key và vẫn bắt đầu bằng id mà claim table key theo.
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

        // 3. Mệnh đề mạnh nhất M2 sống qua conversion: không telemetry row nào thiếu claim. TimescaleDB
        //    chấp nhận foreign key từ hypertable sang table thường là thứ plan §C05 bảo check thay vì
        //    giả định — nó làm được, trên 2.29.2-pg17.
        var foreignKey = await ReadAsync(
            dataSource,
            """
            SELECT confrelid::regclass::text
            FROM pg_constraint
            WHERE conrelid = 'ts.telemetry_measurement'::regclass AND contype = 'f';
            """);

        foreignKey.ShouldBe(["ingest.processed_message"]);

        // 4. Write path vẫn hoạt động end-to-end, đưa row vào chunk thay vì heap.
        var metrics = new IngestionMetrics();
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)),
            metrics);

        // Cách nhau ba ngày theo device clock, nên không thể vào cùng một chunk.
        var messages = new[]
        {
            Message(new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero)),
            Message(new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero)),
            Message(new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero)),
        };

        // Hai trong ba point lùi hơn một chunk so với lúc được ghi, là toàn bộ điểm chính của fixture:
        // chúng rơi vào chunk cũ. Đó cũng đúng là thứ retention-risk counter đếm, nên ở đây báo hai
        // thay vì nothing.
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
        // scope.md §8.4 yêu cầu mọi migration có đường quay lại, và đường chưa từng chạy chỉ là file,
        // không phải rollback. Cái này hơn cả DROP: không có lời gọi un-hypertable table, nên nó copy
        // rồi swap — chi phí đó đáng biết trước khi table có một trăm triệu row, không phải sau.
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

        // Upgrade áp dụng mọi migration sau đó. Rollback theo thứ tự ngược để test chứng minh chain
        // được hỗ trợ thay vì dựa vào DROP TABLE dọn policy side effect.
        //
        // Chain được liệt kê bằng tay, nên mọi migration thêm DEPENDANT cũng phải thêm ở đây. Migration
        // 013 build continuous aggregate thứ hai trên ts.process_signal_1m; bỏ nó khiến test fail với
        // "cannot drop view ts.process_signal_1m because other objects depend on it" — test đang làm
        // đúng việc. 012 nằm trên archive table và không có ordering constraint với chúng, nhưng vẫn đi
        // đầu cùng lý do: mới nhất down trước, luôn vậy.
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

        // Rollback cũng phải trả lại shape M2, nếu không forward migration không chạy lại được: nó gọi
        // tên constraint mà nó drop.
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
        // Regression C05 đưa vào và N-M3-10 mở lại. Mỗi writer từng claim trước rồi race để tạo chunk.
        // TimescaleDB copy telemetry foreign key khi tạo chunk, nên yêu cầu ShareRowExclusive của nó
        // trên claim table tạo cycle với lock RowExclusive mà mọi writer đã giữ ở đó.
        //
        // Path đã fix pre-create slice thiếu trước khi transaction claim nào bắt đầu. Batch phải đến
        // đúng một lần và tạo chunk cần zero write-transaction retry; chấp nhận count khác zero ở đây
        // sẽ đặt lock order cũ trở lại sau retry loop.
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

        // Một message, bốn nghìn reading cách nhau một millisecond: một ngày, vậy một chunk, và chunk
        // đó chưa tồn tại.
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

        // Compose stack tạo extension từ deploy/postgres/init; bare container chưa chạy nó, và mọi
        // migration từ 003 trở đi cần extension này.
        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ExecuteAsync(dataSource, "CREATE EXTENSION IF NOT EXISTS timescaledb;");

        return postgres;
    }

    /// <summary>Rollback script được đưa tới output directory dưới dạng content.</summary>
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

    /// <summary>Chạy query và trả về row đầu dưới dạng string, cho shape assertion.</summary>
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
