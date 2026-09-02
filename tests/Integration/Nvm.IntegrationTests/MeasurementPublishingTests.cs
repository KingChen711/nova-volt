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
/// Phần join mà R-M1-6 nói sẽ chỉ trở nên hữu hình ở M2. Dedup thiết bị dùng key
/// <c>source_event_id</c> còn dedup command dùng key <c>ce_id</c>; nếu hai giá trị đó từng khác
/// nhau, cả hai cơ chế vẫn tiếp tục hoạt động mà không cơ chế nào bảo vệ cơ chế còn lại.
/// </summary>
public sealed class MeasurementPublishingTests
{
    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");
    private static readonly DateTimeOffset RecordedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FileReadAt = RecordedAt.AddSeconds(-10);

    /// <summary>Curve mà một cycler báo cáo suốt cả chu kỳ. Là telemetry, dù có đúng thêm điều gì khác về nó.</summary>
    private const string Capacity = "Formation/Capacity";

    /// <summary>Capacity mà một cell kết thúc ở đó, theo báo cáo của một test station. Một signal khác.</summary>
    private const string CapacityResult = "Formation/CapacityResult";

    private const string Telemetry = "Formation/Voltage";
    private const string CellId = "NV1CL16238A00123";

    [Fact]
    public async Task RawFormationCapacity_IsStoredAsTelemetryAndNotAnnounced()
    {
        // Simulator báo cáo giá trị này xuyên suốt chu kỳ. Đây là một signal khác với result, nên nó
        // không thể tới được bus qua bất kỳ route nào: whitelist không bao giờ nêu tên nó.
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
        // M7 map một channel với cell đang nằm trong đó, và từ ngày đó mỗi điểm trên curve đều mang
        // theo một unit id. Một hệ thống đọc tính "đã hoàn tất" từ unit id sẽ bắt đầu announce cả
        // curve ngay từ ngày mapping đó xuất hiện - hàng trăm nghìn "result" mỗi ca, mỗi cái là một
        // điểm giữa chừng của một lần charge chưa ai chấm điểm xong.
        //
        // Không có gì thay đổi ở row này ngoài việc giờ nó đã biết cell của mình. Nó vẫn là
        // telemetry, và signal code chính là thứ nói lên điều đó.
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
        // Nửa còn lại của quy tắc. Signal code khiến đây là một business fact, nhưng một event chấm
        // điểm cho một cell thì phải nêu tên cell đó - nên một result tới mà không có nó là
        // malformed, và phản ứng trung thực là giữ lại reading và không nói gì thay vì announce một
        // điểm số về không ai cả.
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

        // Toàn bộ mục đích của C14: identity trong CloudEvents header tới từ đúng natural key đã
        // được commit trong ingestion transaction.
        var storedId = await fixture.ReadSourceEventIdAsync(CapacityResult, TestContext.Current.CancellationToken);
        published.EventId.ShouldBe(storedId);

        published.EventId.ShouldBe(
            evaluated.Reading.NaturalKey(evaluated.EquipmentPath, evaluated.UnitId).SourceEventId.Value);
    }

    [Fact]
    public async Task PublishFailure_LeavesTheTelemetryRowsAndIsCounted()
    {
        // Dual write mà ADR-022 đã đo được ở mức 18/200. M2 không đóng nó lại — outbox của M6 mới
        // làm việc đó — nên yêu cầu ở đây là nó phải fail theo hướng trung thực: reading vẫn sống
        // sót và hệ thống nói rõ bus đang chậm bao xa.
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
        // Một duplicate không lưu gì, nên nó cũng không được announce gì. Publish trên duplicate
        // path sẽ đặt cùng một ce_id lên bus hai lần — vô hại với một handler idempotent nhưng gây
        // hiểu lầm hoàn toàn cho bất kỳ ai đếm event theo row.
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

    // File-drop path là con đường duy nhất mang theo unit id ở M2, điều này khiến nó là cách duy
    // nhất để dựng một row biết được cell của mình - kể cả raw-curve row mà M7 rồi sẽ tạo ra.
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
