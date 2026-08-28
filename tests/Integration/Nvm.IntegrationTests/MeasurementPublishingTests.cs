using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using Nvm.Contracts.Events.Quality;
using Nvm.Ingestion;
using Nvm.Ingestion.FileDrop;
using Nvm.Ingestion.Persistence;
using Nvm.Ingestion.Publishing;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// The join R-M1-6 said would only become visible at M2. Device deduplication keys on
/// <c>source_event_id</c> and command deduplication keys on <c>ce_id</c>; if those two are ever
/// different values, both mechanisms carry on working and neither protects the other.
/// </summary>
public sealed class MeasurementPublishingTests
{
    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");
    private static readonly DateTimeOffset RecordedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FileReadAt = RecordedAt.AddSeconds(-10);

    /// <summary>The curve a cycler reports all cycle long. Telemetry, whatever else is true of it.</summary>
    private const string Capacity = "Formation/Capacity";

    /// <summary>The capacity a cell finished at, as a test station reports it. A different signal.</summary>
    private const string CapacityResult = "Formation/CapacityResult";

    private const string Telemetry = "Formation/Voltage";
    private const string CellId = "NV1CL16238A00123";

    [Fact]
    public async Task RawFormationCapacity_IsStoredAsTelemetryAndNotAnnounced()
    {
        // The simulator reports this value throughout the cycle. It is a different signal from the
        // result, so it cannot reach the bus by any route: the whitelist never names it.
        await using var fixture = await IngestionFixture.StartAsync(new PublishedSignals([CapacityResult]));

        var result = await fixture.Ingestor.IngestAsync(
            [SparkplugMessage(Capacity, new MetricValue.Real(4.812))],
            TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(1);
        result.PublishFailures.ShouldBe(0);
        (await fixture.CountTelemetryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        fixture.Publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task RawFormationCapacityThatKnowsItsCell_IsStillNotAnnounced()
    {
        // M7 maps a channel to the cell sitting in it, and from that day every point on the curve
        // carries a unit id. A system that read finality off the unit id would start announcing the
        // whole curve on the day that mapping landed - hundreds of thousands of "results" a shift,
        // each one a midpoint of a charge nobody has finished grading.
        //
        // Nothing about this row changed except that it now knows its cell. It is still telemetry,
        // and the signal code is what says so.
        await using var fixture = await IngestionFixture.StartAsync(new PublishedSignals([CapacityResult]));

        var result = await fixture.Ingestor.IngestAsync(
            [CsvRow(Capacity, CellId)],
            FileReadAt,
            TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(1);
        (await fixture.CountTelemetryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        fixture.Publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEvaluatedResultWithoutACell_IsStoredAndLeftUnannounced()
    {
        // The other half of the rule. The signal code makes this a business fact, but an event that
        // grades a cell has to name the cell - so a result that arrives without one is malformed,
        // and the honest response is to keep the reading and say nothing rather than announce a
        // grade about nobody.
        await using var fixture = await IngestionFixture.StartAsync(new PublishedSignals([CapacityResult]));

        var result = await fixture.Ingestor.IngestAsync(
            [SparkplugMessage(CapacityResult, new MetricValue.Real(4.812))],
            TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(1);
        (await fixture.CountTelemetryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        fixture.Publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task EvaluatedCsvResultWithUnitId_IsAnnouncedWithStoredSourceEventId()
    {
        await using var fixture = await IngestionFixture.StartAsync(new PublishedSignals([CapacityResult]));
        var evaluated = EvaluatedCapacityResult();

        var result = await fixture.Ingestor.IngestAsync(
            [evaluated],
            FileReadAt,
            TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(1);
        result.PublishFailures.ShouldBe(0);
        (await fixture.CountTelemetryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var published = fixture.Publisher.Published.ShouldHaveSingleItem();
        published.SignalCode.ShouldBe(CapacityResult);
        published.UnitId.ShouldBe(CellId);
        published.RealValue.ShouldBe(4.812);

        // The whole point of C14: the identity in the CloudEvents header comes from the same natural
        // key already committed in the ingestion transaction.
        var storedId = await fixture.ReadSourceEventIdAsync(CapacityResult, TestContext.Current.CancellationToken);
        published.EventId.ShouldBe(storedId);

        published.EventId.ShouldBe(
            evaluated.Reading.NaturalKey(evaluated.EquipmentPath, evaluated.UnitId).SourceEventId.Value);
    }

    [Fact]
    public async Task PublishFailure_LeavesTheTelemetryRowsAndIsCounted()
    {
        // The dual write ADR-022 measured at 18/200. M2 does not close it — M6's outbox does — so the
        // requirement here is that it fails in the honest direction: the reading survives and the
        // system says how far behind the bus is.
        await using var fixture = await IngestionFixture.StartAsync(new PublishedSignals([CapacityResult]));
        fixture.Publisher.FailEverything = true;

        var result = await fixture.Ingestor.IngestAsync(
            [EvaluatedCapacityResult()],
            FileReadAt,
            TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(1);
        result.PublishFailures.ShouldBe(1);
        fixture.Metrics.PublishFailureCount.ShouldBe(1);
        (await fixture.CountTelemetryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task NoWhitelistedSignals_PublishesNothingAtAll()
    {
        await using var fixture = await IngestionFixture.StartAsync(PublishedSignals.None);

        await fixture.Ingestor.IngestAsync(
            [SparkplugMessage(CapacityResult, new MetricValue.Real(4.9)), SparkplugMessage(Telemetry, new MetricValue.Real(3.7))],
            TestContext.Current.CancellationToken);

        fixture.Publisher.Published.ShouldBeEmpty();
        (await fixture.CountTelemetryAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task ARepeatedDelivery_IsNotAnnouncedTwice()
    {
        // A duplicate stores nothing, so it must announce nothing. Publishing on the duplicate path
        // would put the same ce_id on the bus twice — harmless for an idempotent handler and
        // completely misleading for anyone counting events against rows.
        await using var fixture = await IngestionFixture.StartAsync(new PublishedSignals([CapacityResult]));
        var result = EvaluatedCapacityResult();

        await fixture.Ingestor.IngestAsync([result], FileReadAt, TestContext.Current.CancellationToken);
        var second = await fixture.Ingestor.IngestAsync(
            [result],
            FileReadAt,
            TestContext.Current.CancellationToken);

        second.Inserted.ShouldBe(0);
        second.Duplicates.ShouldBe(1);
        fixture.Publisher.Published.Count.ShouldBe(1);
    }

    private static DecodedSparkplugMessage SparkplugMessage(string signalCode, MetricValue value) =>
        new(
            "NV1",
            Channel,
            SparkplugTopic.For(Channel, SparkplugMessageType.DeviceData),
            RecordedAt.AddSeconds(-30),
            ImmutableArray.Create(
                new DeviceReading(signalCode, Alias: 1, value, RecordedAt.AddSeconds(-40))));

    private static FileMeasurement EvaluatedCapacityResult() => CsvRow(CapacityResult, CellId);

    // The file-drop path is the only one that carries a unit id in M2, which makes it the only way
    // to build a row that knows its cell - including the raw-curve row M7 will eventually produce.
    private static FileMeasurement CsvRow(string signalCode, string unitId)
    {
        var line = string.Join(
            ',',
            Channel.Value,
            unitId,
            signalCode,
            "2026-08-28T11:59:20.000Z",
            "real",
            "4.812");

        return new CsvMeasurementReader(new OneChannelDirectory())
            .Read([CsvMeasurementReader.Header, line])
            .Measurements
            .ShouldHaveSingleItem();
    }

    private sealed class IngestionFixture : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _postgres;
        private readonly NpgsqlDataSource _dataSource;

        private IngestionFixture(
            PostgreSqlContainer postgres,
            NpgsqlDataSource dataSource,
            IngestionMetrics metrics,
            RecordingPublisher publisher,
            PostgresMeasurementIngestor ingestor)
        {
            _postgres = postgres;
            _dataSource = dataSource;
            Metrics = metrics;
            Publisher = publisher;
            Ingestor = ingestor;
        }

        internal IngestionMetrics Metrics { get; }

        internal RecordingPublisher Publisher { get; }

        internal PostgresMeasurementIngestor Ingestor { get; }

        internal static async Task<IngestionFixture> StartAsync(PublishedSignals signals)
        {
            var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
                .WithDatabase("novavolt_integration")
                .WithUsername("nvm")
                .WithPassword("nvm_integration_only")
                .Build();

            await postgres.StartAsync(CancellationToken.None);
            IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

            var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
            var metrics = new IngestionMetrics();
            var publisher = new RecordingPublisher();

            return new IngestionFixture(
                postgres,
                dataSource,
                metrics,
                publisher,
                new PostgresMeasurementIngestor(
                    dataSource,
                    new FakeTimeProvider(RecordedAt),
                    metrics,
                    publisher: publisher,
                    publishedSignals: signals));
        }

        internal async Task<long> CountTelemetryAsync(CancellationToken cancellationToken)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM ts.telemetry_measurement;",
                connection);

            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        internal async Task<Guid> ReadSourceEventIdAsync(string signalCode, CancellationToken cancellationToken)
        {
            const string Sql = """
                SELECT t.source_event_id
                FROM ts.telemetry_measurement t
                JOIN ingest.processed_message p USING (source_event_id)
                WHERE t.signal_code = @signal_code;
                """;

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(Sql, connection);
            command.Parameters.AddWithValue("signal_code", NpgsqlDbType.Text, signalCode);

            return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }

    private sealed class RecordingPublisher : IMeasurementEventPublisher
    {
        internal List<MeasurementRecorded> Published { get; } = [];

        internal bool FailEverything { get; set; }

        public Task<int> PublishAsync(
            IReadOnlyCollection<MeasurementRecorded> events,
            CancellationToken cancellationToken)
        {
            if (FailEverything)
            {
                return Task.FromResult(events.Count);
            }

            Published.AddRange(events);
            return Task.FromResult(0);
        }
    }

    private sealed class OneChannelDirectory : IEquipmentDirectory
    {
        public bool Contains(EquipmentPath path) => path == Line || path == Channel;

        public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode) =>
            line == Line && string.Equals(deviceCode, Channel.Code, StringComparison.Ordinal)
                ? Channel
                : null;
    }
}
