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

public sealed class IngestionDeduplicationTests
{
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    [Fact]
    public async Task PostgreSql_DeduplicatesGloballyAndRollsBackTheClaimWithItsEffect()
    {
        await using var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
            .WithDatabase("novavolt_integration")
            .WithUsername("nvm")
            .WithPassword("nvm_integration_only")
            .Build();

        await postgres.StartAsync(CancellationToken.None);
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 31, 23, 59, 0, TimeSpan.Zero));
        var metrics = new IngestionMetrics();
        var ingestor = new PostgresMeasurementIngestor(dataSource, clock, metrics);

        // Same logical reading, first seen in January and replayed after the calendar has moved to
        // June. A first_seen_at partition must not make this key new again (ADR-030).
        var repeated = Message("Formation/Voltage", new DateTimeOffset(2026, 1, 31, 23, 58, 50, TimeSpan.Zero));
        var first = await ingestor.IngestAsync([repeated], CancellationToken.None);
        clock.SetUtcNow(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var second = await ingestor.IngestAsync([repeated], CancellationToken.None);
        var third = await ingestor.IngestAsync([repeated], CancellationToken.None);

        first.ShouldBe(new IngestionResult(1, 0));
        second.ShouldBe(new IngestionResult(0, 1));
        third.ShouldBe(new IngestionResult(0, 1));
        metrics.DuplicateCount.ShouldBe(2);
        (await CountRowsAsync(dataSource, SourceIds(repeated), CancellationToken.None))
            .ShouldBe((Processed: 1L, Telemetry: 1L));

        // Device timestamp is part of the natural key. Everything else is deliberately identical.
        var at = new DateTimeOffset(2026, 6, 1, 1, 0, 0, TimeSpan.Zero);
        var earlier = Message("Formation/Current", at);
        var later = Message("Formation/Current", at.AddSeconds(1));
        var timestampResult = await ingestor.IngestAsync([earlier, later], CancellationToken.None);

        timestampResult.ShouldBe(new IngestionResult(2, 0));
        (await CountRowsAsync(dataSource, SourceIds(earlier, later), CancellationToken.None))
            .ShouldBe((Processed: 2L, Telemetry: 2L));

        // The claim insert has already run when the signal-length constraint rejects the telemetry
        // insert. Disposing the failed transaction must remove both, or retry would be lost forever.
        var invalid = Message(new string('X', 257), at.AddSeconds(2));
        var invalidId = SourceIds(invalid);

        await Should.ThrowAsync<PostgresException>(() =>
            ingestor.IngestAsync([invalid], CancellationToken.None));

        (await CountRowsAsync(dataSource, invalidId, CancellationToken.None))
            .ShouldBe((Processed: 0L, Telemetry: 0L));
        metrics.InsertedCount.ShouldBe(3);
        metrics.DuplicateCount.ShouldBe(2);
    }

    private static DecodedSparkplugMessage Message(string signalCode, DateTimeOffset deviceTimestamp)
    {
        var topic = SparkplugTopic.For(Channel, SparkplugMessageType.DeviceData);
        var reading = new DeviceReading(
            signalCode,
            Alias: 1,
            new MetricValue.Real(3.7),
            deviceTimestamp);

        return new DecodedSparkplugMessage(
            "NV1",
            Channel,
            topic,
            deviceTimestamp.AddSeconds(10),
            ImmutableArray.Create(reading));
    }

    private static Guid[] SourceIds(params DecodedSparkplugMessage[] messages) =>
        [.. messages.SelectMany(message =>
            message.Readings.Select(reading => reading.NaturalKey(message.EquipmentPath).SourceEventId.Value))];

    private static async Task<(long Processed, long Telemetry)> CountRowsAsync(
        NpgsqlDataSource dataSource,
        Guid[] sourceEventIds,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT
                (SELECT count(*) FROM ingest.processed_message WHERE source_event_id = ANY(@ids)),
                (SELECT count(*) FROM ts.telemetry_measurement WHERE source_event_id = ANY(@ids));
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, sourceEventIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        (await reader.ReadAsync(cancellationToken)).ShouldBeTrue();

        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
