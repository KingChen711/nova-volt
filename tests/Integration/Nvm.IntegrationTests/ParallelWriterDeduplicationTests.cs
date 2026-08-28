using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// Splitting one batch across several writers is what let ingestion pass N1, and it is also the
/// only change in M2 that gives up a single transaction per batch. What must survive is the
/// property D1 is stated in: a measurement is stored exactly once, no matter how the rows were
/// distributed across connections or how many times the gateway replays the batch afterwards.
/// </summary>
public sealed class ParallelWriterDeduplicationTests
{
    private const int Writers = 4;
    private const int MinRowsPerWriter = 256;

    // Comfortably past Writers * MinRowsPerWriter so the batch really is split rather than
    // quietly falling back to the single-writer path.
    private const int Readings = 4_000;

    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    [Fact]
    public async Task ABatchSplitAcrossWriters_StoresEveryMeasurementExactlyOnceUnderReplay()
    {
        await using var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
            .WithDatabase("novavolt_integration")
            .WithUsername("nvm")
            .WithPassword("nvm_integration_only")
            .Build();

        await postgres.StartAsync(TestContext.Current.CancellationToken);
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero));
        var metrics = new IngestionMetrics();
        var ingestor = new PostgresMeasurementIngestor(
            dataSource,
            clock,
            metrics,
            writerParallelism: Writers,
            minRowsPerWriter: MinRowsPerWriter);

        var batch = Batch(Readings);
        var ids = SourceIds(batch);
        ids.Distinct().Count().ShouldBe(Readings);

        var first = await ingestor.IngestAsync([batch], TestContext.Current.CancellationToken);

        first.ShouldBe(new IngestionResult(Readings, 0));
        (await CountAsync(dataSource, ids)).ShouldBe((Processed: (long)Readings, Telemetry: (long)Readings));

        // At-least-once is the contract on the conduit, so the same batch arriving again is normal
        // rather than exceptional. Every row must come back as a duplicate and nothing may be
        // written a second time, which is what makes a partial commit safe to retry.
        var replay = await ingestor.IngestAsync([batch], TestContext.Current.CancellationToken);

        replay.ShouldBe(new IngestionResult(0, Readings));
        (await CountAsync(dataSource, ids)).ShouldBe((Processed: (long)Readings, Telemetry: (long)Readings));
        metrics.InsertedCount.ShouldBe(Readings);
        metrics.DuplicateCount.ShouldBe(Readings);
    }

    [Fact]
    public async Task WritersDisagreeingWithOneWriter_WouldChangeTheStoredResult()
    {
        await using var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
            .WithDatabase("novavolt_integration")
            .WithUsername("nvm")
            .WithPassword("nvm_integration_only")
            .Build();

        await postgres.StartAsync(TestContext.Current.CancellationToken);
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero));

        var batch = Batch(Readings);
        var ids = SourceIds(batch);

        // The same input through one writer and through four has to land identically; the split is
        // a throughput decision and must not be observable in the data.
        var serial = new PostgresMeasurementIngestor(dataSource, clock, new IngestionMetrics());
        var serialResult = await serial.IngestAsync([batch], TestContext.Current.CancellationToken);

        var parallel = new PostgresMeasurementIngestor(
            dataSource,
            clock,
            new IngestionMetrics(),
            writerParallelism: Writers,
            minRowsPerWriter: MinRowsPerWriter);
        var parallelResult = await parallel.IngestAsync([batch], TestContext.Current.CancellationToken);

        serialResult.ShouldBe(new IngestionResult(Readings, 0));
        parallelResult.ShouldBe(new IngestionResult(0, Readings));
        (await CountAsync(dataSource, ids)).ShouldBe((Processed: (long)Readings, Telemetry: (long)Readings));
    }

    // One message carrying many readings: that is how a real DDATA batch reaches the ingestor, and
    // it is the shape the writer split actually divides.
    private static DecodedSparkplugMessage Batch(int readings)
    {
        var at = new DateTimeOffset(2026, 8, 29, 7, 0, 0, TimeSpan.Zero);
        var builder = ImmutableArray.CreateBuilder<DeviceReading>(readings);

        for (var index = 0; index < readings; index++)
        {
            builder.Add(new DeviceReading(
                "Formation/Voltage",
                Alias: 1,
                new MetricValue.Real(3.7),
                at.AddMilliseconds(index)));
        }

        return new DecodedSparkplugMessage(
            "NV1",
            Channel,
            SparkplugTopic.For(Channel, SparkplugMessageType.DeviceData),
            at.AddMinutes(1),
            builder.DrainToImmutable());
    }

    private static Guid[] SourceIds(DecodedSparkplugMessage message) =>
        [.. message.Readings.Select(reading =>
            reading.NaturalKey(message.EquipmentPath).SourceEventId.Value)];

    private static async Task<(long Processed, long Telemetry)> CountAsync(
        NpgsqlDataSource dataSource,
        Guid[] sourceEventIds)
    {
        const string Sql = """
            SELECT
                (SELECT count(*) FROM ingest.processed_message WHERE source_event_id = ANY(@ids)),
                (SELECT count(*) FROM ts.telemetry_measurement WHERE source_event_id = ANY(@ids));
            """;

        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, sourceEventIds);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
