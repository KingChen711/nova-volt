using System.Collections.Immutable;
using System.Diagnostics;
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
/// Việc tách một batch ra nhiều writer chính là thứ giúp ingestion vượt qua N1, và cũng là thay đổi
/// duy nhất trong M2 từ bỏ việc dùng một transaction duy nhất cho mỗi batch. Điều bắt buộc phải sống
/// sót là property được nêu trong D1: một measurement được lưu đúng một lần, bất kể các row được
/// phân phối thế nào qua các connection hay gateway đã replay batch bao nhiêu lần sau đó.
/// </summary>
public sealed class ParallelWriterDeduplicationTests
{
    private const int Writers = 4;
    private const int MinRowsPerWriter = 256;

    // Vượt xa Writers * MinRowsPerWriter để batch thực sự được tách ra thay vì âm thầm rơi trở lại
    // con đường single-writer.
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

        // At-least-once là contract trên conduit, nên cùng một batch tới lần nữa là chuyện bình
        // thường chứ không phải ngoại lệ. Mọi row phải quay lại dưới dạng duplicate và không gì được
        // ghi lần thứ hai, đó chính là điều khiến một partial commit an toàn để retry.
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

        // Cùng một input đi qua một writer và đi qua bốn writer phải cho kết quả giống hệt nhau;
        // việc tách ra là một quyết định về throughput và không được phép quan sát thấy trong dữ
        // liệu.
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

    [Fact]
    public async Task ConcurrentBatchesAcrossFreshDays_PrecreateEveryChunkBeforeClaiming()
    {
        const int FreshDays = 16;
        const int BatchesPerDay = 8;

        await using var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
            .WithDatabase("novavolt_integration")
            .WithUsername("nvm")
            .WithPassword("nvm_integration_only")
            .Build();

        await postgres.StartAsync(TestContext.Current.CancellationToken);
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var metrics = new IngestionMetrics();
        var firstDay = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var elapsedMilliseconds = new long[FreshDays];
        var messages = new List<DecodedSparkplugMessage>(FreshDays * BatchesPerDay);

        for (var dayIndex = 0; dayIndex < FreshDays; dayIndex++)
        {
            var day = firstDay.AddDays(dayIndex);
            var ingestor = new PostgresMeasurementIngestor(
                dataSource,
                new FakeTimeProvider(day.AddHours(1)),
                metrics);
            var daily = Enumerable.Range(0, BatchesPerDay)
                .Select(batchIndex => Batch(1, day.AddMinutes(10).AddMilliseconds(batchIndex)))
                .ToArray();
            messages.AddRange(daily);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = daily.Select(async message =>
            {
                await start.Task;
                return await ingestor.IngestAsync([message], TestContext.Current.CancellationToken);
            }).ToArray();

            var stopwatch = Stopwatch.StartNew();
            start.SetResult();
            var results = await Task.WhenAll(writes);
            stopwatch.Stop();
            elapsedMilliseconds[dayIndex] = stopwatch.ElapsedMilliseconds;

            results.ShouldAllBe(result => result.Inserted == 1 && result.Duplicates == 0);
        }

        var ids = messages.SelectMany(SourceIds).ToArray();
        var expected = (long)(FreshDays * BatchesPerDay);
        (await CountAsync(dataSource, ids)).ShouldBe((Processed: expected, Telemetry: expected));
        (await CountChunksAsync(dataSource, firstDay, firstDay.AddDays(FreshDays))).ShouldBe(FreshDays);
        metrics.WriteRetryCount.ShouldBe(0);

        // Một replay bình thường không được thêm chunk. Regression test riêng cho retention-policy
        // drop một chunk cũ trong khi vẫn giữ lại global claim của nó và chứng minh rằng replay
        // không tạo lại nó; stress case này giữ concurrent path bị giới hạn ở tám connection cùng
        // lúc.
        var replayIngestor = new PostgresMeasurementIngestor(
            dataSource,
            new FakeTimeProvider(firstDay.AddDays(FreshDays)),
            metrics);
        var replayResults = new List<IngestionResult>(messages.Count);
        foreach (var replayDay in messages.Chunk(BatchesPerDay))
        {
            var replayStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replays = replayDay.Select(async message =>
            {
                await replayStart.Task;
                return await replayIngestor.IngestAsync([message], TestContext.Current.CancellationToken);
            }).ToArray();

            replayStart.SetResult();
            replayResults.AddRange(await Task.WhenAll(replays));
        }

        replayResults.ShouldAllBe(result => result.Inserted == 0 && result.Duplicates == 1);
        (await CountAsync(dataSource, ids)).ShouldBe((Processed: expected, Telemetry: expected));
        (await CountChunksAsync(dataSource, firstDay, firstDay.AddDays(FreshDays))).ShouldBe(FreshDays);
        metrics.WriteRetryCount.ShouldBe(0);

        var orderedLatency = elapsedMilliseconds.Order().ToArray();
        var p95Index = (int)Math.Ceiling(orderedLatency.Length * 0.95) - 1;
        var evidence = FormattableString.Invariant(
            $"NVM_CHUNK_PRECREATE fresh_days={FreshDays} concurrent_batches={BatchesPerDay} rows={expected} retries={metrics.WriteRetryCount} p95_observation_ms={orderedLatency[p95Index]} max_observation_ms={orderedLatency[^1]}");
        TestContext.Current.TestOutputHelper?.WriteLine(evidence);
    }

    // Một message mang nhiều reading: đó là cách một DDATA batch thật tới được ingestor, và đó cũng
    // là hình dạng mà writer split thực sự chia ra.
    private static DecodedSparkplugMessage Batch(int readings, DateTimeOffset? startsAt = null)
    {
        var at = startsAt ?? new DateTimeOffset(2026, 8, 29, 7, 0, 0, TimeSpan.Zero);
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

    private static async Task<long> CountChunksAsync(
        NpgsqlDataSource dataSource,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        const string Sql = """
            SELECT count(*)
            FROM timescaledb_information.chunks
            WHERE hypertable_schema = 'ts'
              AND hypertable_name = 'telemetry_measurement'
              AND range_start >= @range_start
              AND range_end <= @range_end;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("range_start", NpgsqlDbType.TimestampTz, rangeStart);
        command.Parameters.AddWithValue("range_end", NpgsqlDbType.TimestampTz, rangeEnd);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
