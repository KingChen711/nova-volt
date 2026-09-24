using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Contracts.Events.Quality;
using Nvm.Ingestion;
using Nvm.Ingestion.FileDrop;
using Nvm.Ingestion.Persistence;
using Nvm.Ingestion.Publishing;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

public sealed class PostgresOutboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    [Fact]
    public async Task PartialWriterCommit_RetryPreservesOneDurableIntentPerEligibleRow()
    {
        await using var fixture = await Fixture.StartAsync();
        var rows = new[] { Fixture.Measurement("GoodResult"), Fixture.Measurement("BadResult") };
        var ingestor = fixture.Ingestor(new PublishedSignals(["GoodResult", "BadResult"]), writers: 2);

        await fixture.ExecuteAsync("""
            CREATE FUNCTION ingest.reject_bad_result() RETURNS trigger LANGUAGE plpgsql AS '
            BEGIN
                IF NEW.signal_code = ''BadResult'' THEN
                    RAISE EXCEPTION ''forced writer failure'';
                END IF;
                RETURN NEW;
            END';
            CREATE TRIGGER reject_bad_result BEFORE INSERT ON ts.telemetry_measurement
                FOR EACH ROW EXECUTE FUNCTION ingest.reject_bad_result();
            """);

        await Should.ThrowAsync<PostgresException>(() =>
            ingestor.IngestAsync(rows, Now, TestContext.Current.CancellationToken));

        (await fixture.CountAsync("ts.telemetry_measurement")).ShouldBe(1);
        (await fixture.CountAsync("ingest.measurement_outbox")).ShouldBe(1);
        fixture.Publisher.Published.ShouldBeEmpty();

        await fixture.ExecuteAsync("DROP TRIGGER reject_bad_result ON ts.telemetry_measurement;");
        var retried = await ingestor.IngestAsync(rows, Now, TestContext.Current.CancellationToken);
        retried.Inserted.ShouldBe(1);
        retried.Duplicates.ShouldBe(1);
        (await fixture.CountAsync("ts.telemetry_measurement")).ShouldBe(2);
        (await fixture.CountAsync("ingest.measurement_outbox")).ShouldBe(2);

        var dispatcher = fixture.Dispatcher();
        (await dispatcher.DispatchOneAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await dispatcher.DispatchOneAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await dispatcher.DispatchOneAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        fixture.Publisher.Published.Select(e => e.EventId).Distinct().Count().ShouldBe(2);
        (await fixture.CountAsync("ingest.measurement_outbox WHERE published_at IS NULL")).ShouldBe(0);
    }

    [Fact]
    public async Task FailedPublish_RemainsDurableUntilANewDispatcherCanRetry()
    {
        await using var fixture = await Fixture.StartAsync();
        var row = Fixture.Measurement("GoodResult");
        var ingestor = fixture.Ingestor(new PublishedSignals(["GoodResult"]));
        await ingestor.IngestAsync([row], Now, TestContext.Current.CancellationToken);

        fixture.Publisher.Fail = true;
        (await fixture.Dispatcher().DispatchOneAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await fixture.CountAsync("ingest.measurement_outbox WHERE published_at IS NULL")).ShouldBe(1);
        (await fixture.Dispatcher().DispatchOneAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Publisher.Fail = false;
        (await fixture.Dispatcher().DispatchOneAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        fixture.Publisher.Published.ShouldHaveSingleItem().EventId.ShouldBe(
            row.Reading.NaturalKey(row.EquipmentPath, row.UnitId).SourceEventId.Value);
        (await fixture.CountAsync("ingest.measurement_outbox WHERE published_at IS NULL")).ShouldBe(0);

        var replay = await ingestor.IngestAsync([row], Now, TestContext.Current.CancellationToken);
        replay.Inserted.ShouldBe(0);
        (await fixture.CountAsync("ingest.measurement_outbox")).ShouldBe(1);
        (await fixture.Dispatcher().DispatchOneAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task CommitOfPublishReceiptFails_IntentReplaysWithTheSameEventId()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.Ingestor(new PublishedSignals(["GoodResult"]))
            .IngestAsync([Fixture.Measurement("GoodResult")], Now, TestContext.Current.CancellationToken);

        await fixture.ExecuteAsync("""
            CREATE FUNCTION ingest.reject_outbox_receipt() RETURNS trigger LANGUAGE plpgsql AS '
            BEGIN
                RAISE EXCEPTION ''forced receipt commit failure'';
            END';
            CREATE TRIGGER reject_outbox_receipt BEFORE UPDATE ON ingest.measurement_outbox
                FOR EACH ROW EXECUTE FUNCTION ingest.reject_outbox_receipt();
            """);

        await Should.ThrowAsync<PostgresException>(() =>
            fixture.Dispatcher().DispatchOneAsync(TestContext.Current.CancellationToken));
        fixture.Publisher.Published.Count.ShouldBe(1);
        (await fixture.CountAsync("ingest.measurement_outbox WHERE published_at IS NULL")).ShouldBe(1);

        await fixture.ExecuteAsync("DROP TRIGGER reject_outbox_receipt ON ingest.measurement_outbox;");
        (await fixture.Dispatcher().DispatchOneAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        fixture.Publisher.Published.Count.ShouldBe(2);
        fixture.Publisher.Published[0].EventId.ShouldBe(fixture.Publisher.Published[1].EventId);
        (await fixture.CountAsync("ingest.measurement_outbox WHERE published_at IS NULL")).ShouldBe(0);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _postgres;
        private readonly NpgsqlDataSource _dataSource;

        private Fixture(PostgreSqlContainer postgres, NpgsqlDataSource dataSource)
        {
            _postgres = postgres;
            _dataSource = dataSource;
            Clock = new FakeTimeProvider(Now);
            Publisher = new RecordingPublisher();
        }

        internal FakeTimeProvider Clock { get; }
        internal RecordingPublisher Publisher { get; }

        internal static async Task<Fixture> StartAsync()
        {
            var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
                .WithDatabase("novavolt_integration")
                .WithUsername("nvm")
                .WithPassword("nvm_integration_only")
                .Build();
            await postgres.StartAsync(TestContext.Current.CancellationToken);
            IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());
            return new Fixture(postgres, NpgsqlDataSource.Create(postgres.GetConnectionString()));
        }

        internal PostgresMeasurementIngestor Ingestor(PublishedSignals signals, int writers = 1) =>
            new(_dataSource, Clock, new IngestionMetrics(), publisher: Publisher,
                publishedSignals: signals, writerParallelism: writers, minRowsPerWriter: 1,
                useTransactionalOutbox: true);

        internal PostgresMeasurementOutboxDispatcher Dispatcher() =>
            new(_dataSource, Clock, Publisher,
                NullLogger<PostgresMeasurementOutboxDispatcher>.Instance);

        internal static FileMeasurement Measurement(string signalCode)
        {
            var line = string.Join(',', Channel.Value, "NV1CL16238A00123", signalCode,
                "2026-09-23T11:59:20.000Z", "real", "4.812");
            return new CsvMeasurementReader(new Directory())
                .Read([CsvMeasurementReader.Header, line]).Measurements.ShouldHaveSingleItem();
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
#pragma warning disable CA2100 // Only literal test fixture SQL is passed to this private helper.
            await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<long> CountAsync(string tableAndCondition)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
#pragma warning disable CA2100 // Only literal table names/conditions are passed by these tests.
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {tableAndCondition}", connection);
#pragma warning restore CA2100
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }

    private sealed class RecordingPublisher : IMeasurementEventPublisher
    {
        internal bool Fail { get; set; }
        internal List<MeasurementRecorded> Published { get; } = [];

        public Task<int> PublishAsync(IReadOnlyCollection<MeasurementRecorded> events, CancellationToken token)
        {
            if (Fail)
            {
                return Task.FromResult(events.Count);
            }
            Published.AddRange(events);
            return Task.FromResult(0);
        }
    }

    private sealed class Directory : IEquipmentDirectory
    {
        public bool Contains(EquipmentPath path) => path == Channel || path ==
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
        public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode) => null;
    }
}
