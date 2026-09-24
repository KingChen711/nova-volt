using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Nvm.Contracts.Events.Quality;
using Nvm.Ingestion.FileDrop;
using Nvm.Ingestion.Publishing;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.Persistence;

/// <summary>Dùng tính unique của PostgreSQL làm thẩm quyền cho idempotency ở cấp device.</summary>
public sealed class PostgresMeasurementIngestor : IMeasurementIngestor
{
    private const string ClaimSql = """
        WITH incoming AS (
            SELECT *
            FROM unnest(
                @source_event_ids::uuid[],
                @site_ids::text[],
                @natural_keys::text[],
                @first_seen_ats::timestamptz[])
            AS input(source_event_id, site_id, natural_key, first_seen_at)
        )
        INSERT INTO ingest.processed_message (source_event_id, site_id, natural_key, first_seen_at)
        SELECT source_event_id, site_id, natural_key, first_seen_at
        FROM incoming
        ON CONFLICT (source_event_id) DO NOTHING
        RETURNING source_event_id;
        """;

    private const string StoreSql = """
        WITH incoming AS (
            SELECT *
            FROM unnest(
                @source_event_ids::uuid[],
                @site_ids::text[],
                @equipment_ids::text[],
                @unit_ids::text[],
                @step_codes::text[],
                @signal_codes::text[],
                @device_timestamps::timestamptz[],
                @gateway_timestamps::timestamptz[],
                @recorded_ats::timestamptz[],
                @clock_qualities::text[],
                @value_kinds::text[],
                @real_values::float8[],
                @integer_values::bigint[],
                @boolean_values::boolean[],
                @text_values::text[])
            AS input(
                source_event_id, site_id, equipment_id, unit_id, step_code, signal_code,
                device_timestamp, gateway_timestamp, recorded_at, clock_quality, value_kind,
                real_value, integer_value, boolean_value, text_value)
        )
        INSERT INTO ts.telemetry_measurement (
            source_event_id, site_id, equipment_id, unit_id, step_code, signal_code,
            device_timestamp, gateway_timestamp, recorded_at, clock_quality, value_kind,
            real_value, integer_value, boolean_value, text_value)
        SELECT
            source_event_id, site_id, equipment_id, unit_id, step_code, signal_code,
            device_timestamp, gateway_timestamp, recorded_at, clock_quality, value_kind,
            real_value, integer_value, boolean_value, text_value
        FROM incoming;
        """;

    private const string StoreOutboxSql = """
        INSERT INTO ingest.measurement_outbox
            (event_id, site_id, payload, created_at, next_attempt_at)
        VALUES (@event_id, @site_id, @payload::jsonb, @created_at, @created_at);
        """;

    private const string MissingChunkRangesSql = """
        WITH incoming AS (
            SELECT *
            FROM unnest(
                @source_event_ids::uuid[],
                @site_ids::text[],
                @device_timestamps::timestamptz[])
            AS input(source_event_id, site_id, device_timestamp)
        )
        SELECT DISTINCT time_bucket(INTERVAL '1 day', input.device_timestamp) AS range_start
        FROM incoming AS input
        WHERE NOT EXISTS (
            SELECT 1
            FROM ingest.processed_message AS claims
            WHERE claims.site_id = input.site_id
              AND claims.source_event_id = input.source_event_id)
        ORDER BY range_start;
        """;

    private const string EnsureChunkSql = """
        SELECT created
        FROM _timescaledb_functions.create_chunk(
            'ts.telemetry_measurement'::regclass,
            jsonb_build_object(
                'device_timestamp',
                jsonb_build_array(@range_start::timestamptz, @range_start::timestamptz + INTERVAL '1 day')));
        """;

    /// <summary>Số lần một chunk write được phép retry sau một deadlock trước khi bỏ cuộc.</summary>
    /// <remarks>
    /// Có giới hạn, và thấp. Chunk creation được giữ ở ngoài write transaction; một write vẫn thất
    /// bại sau nhiều lần thử là đang tranh chấp với thứ gì khác, và loop trên nó sẽ che giấu điều đó
    /// thay vì báo cáo nó.
    /// </remarks>
    private const int MaxWriteAttempts = 5;

    /// <summary>Một chunk của telemetry hypertable, như migration 003 khai báo.</summary>
    /// <remarks>
    /// Viết cứng ở đây thay vì đọc từ database vì đây là đơn vị mà retention hoạt động theo, và một
    /// counter âm thầm tự suy ra nó sẽ ngừng mang cùng ý nghĩa vào cái ngày ai đó đổi
    /// <c>chunk_time_interval</c>. Nếu ngày đó tới, dòng này phải đổi theo — một cách cố ý.
    /// </remarks>
    private static readonly TimeSpan ChunkInterval = TimeSpan.FromDays(1);

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;
    private readonly IngestionMetrics _metrics;
    private readonly IngestionLag _lag;
    private readonly IMeasurementEventPublisher _publisher;
    private readonly PublishedSignals _publishedSignals;
    private readonly TimeSpan _clockDriftThreshold;
    private readonly int _writerParallelism;
    private readonly int _minRowsPerWriter;
    private readonly bool _useTransactionalOutbox;

    /// <summary>Tạo transaction boundary dùng chung cho cả HTTP lẫn file drop.</summary>
    /// <param name="dataSource">Nguồn connection cho một transaction trên mỗi batch.</param>
    /// <param name="timeProvider">Đóng dấu <c>recorded_at</c> (K1).</param>
    /// <param name="metrics">Các counter chỉ cập nhật sau khi transaction commit.</param>
    /// <param name="lag">Mẫu lag từ device đến database cho D2. Null thì bỏ qua chúng.</param>
    /// <param name="publisher">Nơi các business fact đã commit đi tiếp. Null thì không publish gì.</param>
    /// <param name="publishedSignals">Signal code nào là business fact (scope.md §5.5).</param>
    /// <param name="clockDriftThreshold">
    /// Đồng hồ device và gateway được phép lệch nhau bao xa trước khi một reading bị flag. Mặc định
    /// là <see cref="ClockQualityClassifier.DefaultThreshold"/>.
    /// </param>
    /// <param name="writerParallelism">
    /// Số database writer mà một batch có thể trải rộng ra. Bằng một thì giữ nguyên hành vi
    /// single-transaction.
    /// </param>
    /// <param name="minRowsPerWriter">Số dòng mỗi writer thêm vào phải có để đáng mở ra.</param>
    /// <param name="useTransactionalOutbox">Ghi durable publish intent cùng transaction với telemetry.</param>
    public PostgresMeasurementIngestor(
        NpgsqlDataSource dataSource,
        TimeProvider timeProvider,
        IngestionMetrics metrics,
        IngestionLag? lag = null,
        IMeasurementEventPublisher? publisher = null,
        PublishedSignals? publishedSignals = null,
        TimeSpan? clockDriftThreshold = null,
        int writerParallelism = 1,
        int minRowsPerWriter = 256,
        bool useTransactionalOutbox = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(writerParallelism);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minRowsPerWriter);

        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _lag = lag ?? new IngestionLag();
        _publisher = publisher ?? NullMeasurementEventPublisher.Instance;
        _publishedSignals = publishedSignals ?? PublishedSignals.None;
        _clockDriftThreshold = clockDriftThreshold ?? ClockQualityClassifier.DefaultThreshold;
        _writerParallelism = writerParallelism;
        _minRowsPerWriter = minRowsPerWriter;
        _useTransactionalOutbox = useTransactionalOutbox;
    }

    /// <inheritdoc />
    public async Task<IngestionResult> IngestAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var recordedAt = _timeProvider.GetUtcNow();
        var rawCount = 0;
        var distinct = new Dictionary<Guid, MeasurementRow>();

        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);

            foreach (var reading in message.Readings)
            {
                rawCount++;
                var row = MeasurementRow.FromSparkplug(message, reading, recordedAt, _clockDriftThreshold);
                distinct.TryAdd(row.SourceEventId, row);
            }
        }

        return await StoreAsync(distinct, rawCount, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IngestionResult> IngestAsync(
        IReadOnlyCollection<FileMeasurement> measurements,
        DateTimeOffset readAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var recordedAt = _timeProvider.GetUtcNow();
        var rawCount = 0;
        var distinct = new Dictionary<Guid, MeasurementRow>();

        foreach (var measurement in measurements)
        {
            ArgumentNullException.ThrowIfNull(measurement);

            rawCount++;
            var row = MeasurementRow.FromFileDrop(
                measurement.Reading,
                measurement.EquipmentPath,
                measurement.UnitId,
                readAt,
                recordedAt);
            distinct.TryAdd(row.SourceEventId, row);
        }

        return await StoreAsync(distinct, rawCount, cancellationToken);
    }

    // Transaction boundary mà cả hai adapter đều commit qua. Không cái nào trong chúng được quyết
    // định dedup nghĩa là gì; chúng chỉ quyết định cách đọc.
    private async Task<IngestionResult> StoreAsync(
        Dictionary<Guid, MeasurementRow> distinct,
        int rawCount,
        CancellationToken cancellationToken)
    {
        if (rawCount == 0)
        {
            return new IngestionResult(0, 0);
        }

        MeasurementRow[] rows = [.. distinct.Values];
        await EnsureTelemetryChunksAsync(rows, cancellationToken);

        var chunks = SplitAcrossWriters(rows);
        MeasurementRow[] claimedRows;

        if (chunks.Length == 1)
        {
            claimedRows = await WriteChunkAsync(chunks[0], cancellationToken);
        }
        else
        {
            // Mỗi connection chỉ có một PostgreSQL backend, nên đây là cách duy nhất một batch đơn
            // chạm được tới nhiều hơn một core. Mỗi chunk là một transaction riêng; xem
            // IngestionOptions.WriterParallelism để biết vì sao một commit từng phần vẫn đúng khi retry.
            var written = await Task.WhenAll(
                chunks.Select(chunk => WriteChunkAsync(chunk, cancellationToken)));

            claimedRows = [.. written.SelectMany(chunk => chunk)];
        }

        RecordLag(claimedRows);

        var drifted = claimedRows.Count(row => row.ClockQuality != ClockQuality.Good);
        var result = new IngestionResult(
            claimedRows.Length,
            rawCount - claimedRows.Length,
            drifted,
            RetentionRisk: RecordRetentionRisk(claimedRows));
        _metrics.RecordCommitted(result);

        if (_useTransactionalOutbox)
        {
            // Mỗi writer đã commit telemetry và ý định phát trong cùng một transaction. Dispatcher
            // sẽ gửi cả khi một writer khác hỏng làm Task.WhenAll ném, hoặc process chết ngay đây.
            return result;
        }

        // Sau khi commit, và ở ngoài nó. Một publish thất bại phải để các dòng nguyên tại chỗ: đây là
        // dual-write mà ADR-022 đã đo được ở mức 18/200, và M2 đếm nó thay vì giả vờ rằng outbox
        // đóng lỗ hổng đó (M6) đã có sẵn ở đây.
        var announced = Announce(claimedRows);

        if (announced.Count == 0)
        {
            return result;
        }

        var failures = await _publisher.PublishAsync(announced, cancellationToken);
        _metrics.RecordPublishOutcome(announced.Count, failures);

        return result with { PublishFailures = failures };
    }

    // Việc chia nhỏ tốn một connection và một transaction cho mỗi chunk, chỉ đáng trả cái giá đó khi
    // batch đủ lớn để index maintenance chiếm ưu thế. Batch nhỏ ở lại trên một writer duy nhất.
    private MeasurementRow[][] SplitAcrossWriters(MeasurementRow[] rows)
    {
        var writers = Math.Min(_writerParallelism, rows.Length / _minRowsPerWriter);

        if (writers <= 1)
        {
            return [rows];
        }

        var chunkSize = (rows.Length + writers - 1) / writers;
        var chunks = new MeasurementRow[writers][];

        for (var index = 0; index < writers; index++)
        {
            var start = index * chunkSize;
            var length = Math.Min(chunkSize, rows.Length - start);
            chunks[index] = rows[start..(start + length)];
        }

        return chunks;
    }

    // Claim và store dùng chung một transaction nên một dòng không bao giờ có thể được claim mà
    // không được store.
    private async Task<MeasurementRow[]> WriteChunkAsync(
        MeasurementRow[] rows,
        CancellationToken cancellationToken)
    {
        // Retry thay vì để lộ ra ngoài, vì một serialization failure hay một deadlock không liên quan
        // là một tai nạn lập lịch chứ không phải một phát biểu về dữ liệu. Chunk creation cố ý vắng
        // mặt khỏi transaction này: EnsureTelemetryChunksAsync hoàn tất nó trước khi bất kỳ writer
        // nào lấy RowExclusive lock trên claim table.
        //
        // Retry vẫn an toàn tuyệt đối: PostgreSQL rollback CẢ HAI statement cùng nhau (ADR-030), nên
        // không gì bị claim và không gì bị store, và claim insert là ON CONFLICT DO NOTHING, nên một
        // retry đua với một delivery khác chỉ đơn giản báo cáo các dòng của nó là duplicate.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await WriteChunkOnceAsync(rows, cancellationToken);
            }
            catch (PostgresException failure)
                when (attempt < MaxWriteAttempts && IsTransientWriteConflict(failure))
            {
                _metrics.RecordWriteRetry();
            }
        }
    }

    // TimescaleDB copy foreign key của hypertable sang một chunk mới. Nếu DDL đó chạy sau khi
    // transaction này đã insert claim, các writer đồng thời sẽ tạo thành một lock cycle: mỗi cái giữ
    // RowExclusive trên ingest.processed_message, một cái giữ ShareUpdateExclusive trên hypertable,
    // và chunk creation đòi ShareRowExclusive trên claim table. Pre-create slice trong một
    // autocommit statement loại bỏ cycle đó trong khi vẫn giữ nguyên transaction claim+telemetry.
    //
    // create_chunk an toàn với concurrency: đúng một caller báo created=true và các caller đua nhau
    // cho cùng một slice nhận created=false. Không cache kết quả vô thời hạn; retention có thể xóa
    // một chunk sau đó. Các replay có global claim đã tồn tại được lọc trước để chúng không tạo lại
    // một raw chunk rỗng sau khi retention đã xóa nó một cách hợp lệ.
    private async Task EnsureTelemetryChunksAsync(
        MeasurementRow[] rows,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var ranges = new List<DateTimeOffset>();

        await using (var command = new NpgsqlCommand(MissingChunkRangesSql, connection))
        {
            AddArray(command, "source_event_ids", NpgsqlDbType.Uuid, rows.Select(row => row.SourceEventId).ToArray());
            AddArray(command, "site_ids", NpgsqlDbType.Text, rows.Select(row => row.SiteId).ToArray());
            AddArray(
                command,
                "device_timestamps",
                NpgsqlDbType.TimestampTz,
                rows.Select(row => row.DeviceTimestamp).ToArray());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                ranges.Add(await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken));
            }
        }

        foreach (var rangeStart in ranges)
        {
            await using var command = new NpgsqlCommand(EnsureChunkSql, connection);
            command.Parameters.AddWithValue("range_start", NpgsqlDbType.TimestampTz, rangeStart);

            if (await command.ExecuteScalarAsync(cancellationToken) is not bool)
            {
                throw new InvalidOperationException(
                    "TimescaleDB did not return the chunk-creation result for telemetry_measurement.");
            }
        }
    }

    private async Task<MeasurementRow[]> WriteChunkOnceAsync(
        MeasurementRow[] rows,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var claimedIds = await ClaimAsync(connection, transaction, rows, cancellationToken);
        var claimedRows = rows.Where(row => claimedIds.Contains(row.SourceEventId)).ToArray();

        if (claimedRows.Length > 0)
        {
            await StoreAsync(connection, transaction, claimedRows, cancellationToken);

            if (_useTransactionalOutbox)
            {
                foreach (var measurement in Announce(claimedRows))
                {
                    await using var command = new NpgsqlCommand(StoreOutboxSql, connection, transaction);
                    command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, measurement.EventId);
                    command.Parameters.AddWithValue("site_id", NpgsqlDbType.Text, measurement.SiteId);
                    command.Parameters.AddWithValue("payload", NpgsqlDbType.Text, JsonSerializer.Serialize(measurement));
                    command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, measurement.OccurredAt);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claimedRows;
    }

    // Chỉ hai trạng thái mà PostgreSQL nêu ra khi nó đã undo toàn bộ transaction vì một lý do sẽ
    // không lặp lại. Một constraint violation không thuộc trong hai cái đó, và retry nó sẽ biến một
    // message mà process này phải từ chối thành một vòng lặp vô tận.
    private static bool IsTransientWriteConflict(PostgresException failure) =>
        failure.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure;

    /// <summary>Đếm, theo từng site, các dòng đã commit mà lệch một chunk trở lên so với thời điểm ghi của chúng.</summary>
    /// <remarks>
    /// <para>
    /// ADR-011 đã nợ con số này và migration 007 là lý do nó tới hạn phải trả: raw retention vẫn ở
    /// ngoài lịch cho tới khi có legal hold, và counter này là thứ biến "có nên bật lại không" thành
    /// một phép đo. Một khoảng lệch rộng hơn một chunk nghĩa là dòng đó bị xếp vào một ngày mà nhà máy
    /// không hề sản xuất ra nó, nên retention — thứ xóa nguyên cả chunk theo <c>device_timestamp</c> —
    /// sẽ đánh giá nó theo một ngày mà không ai chọn cả.
    /// </para>
    /// <para>
    /// Giá trị tuyệt đối, không có dấu. Một đồng hồ chạy nhanh một tuần đặt một dòng vào một chunk
    /// còn chưa tồn tại và cũng sai y hệt như một đồng hồ chạy chậm một tuần; chỉ một trong hai
    /// trường hợp từng gần tới retention horizon, nhưng một nhà máy tạo ra một trong hai đều có vấn
    /// đề về đồng hồ đáng để nhìn thấy.
    /// </para>
    /// </remarks>
    private int RecordRetentionRisk(MeasurementRow[] rows)
    {
        Dictionary<string, int>? bySite = null;

        foreach (var row in rows)
        {
            var gap = row.RecordedAt - row.DeviceTimestamp;

            if (gap.Duration() <= ChunkInterval)
            {
                continue;
            }

            bySite ??= new Dictionary<string, int>(StringComparer.Ordinal);
            bySite[row.SiteId] = bySite.GetValueOrDefault(row.SiteId) + 1;
        }

        if (bySite is null)
        {
            return 0;
        }

        var total = 0;

        foreach (var (siteId, count) in bySite)
        {
            _metrics.RecordRetentionRisk(siteId, count);
            total += count;
        }

        return total;
    }

    // Chỉ những đồng hồ tốt. Phép đo này lấy hiệu của hai đồng hồ, nên một PLC lệch hai giờ sẽ đóng
    // góp một "lag" hai giờ không nói lên điều gì về pipeline, và một PLC nhanh hai giờ sẽ đóng góp
    // một giá trị âm. Cả hai đều phá hỏng một percentile theo cách vô hình trong kết quả.
    private void RecordLag(MeasurementRow[] rows)
    {
        foreach (var row in rows)
        {
            if (row.ClockQuality == ClockQuality.Good)
            {
                _lag.Record(row.RecordedAt - row.DeviceTimestamp);
            }
        }
    }

    private List<MeasurementRecorded> Announce(MeasurementRow[] rows)
    {
        if (_publishedSignals.Count == 0)
        {
            return [];
        }

        var announced = new List<MeasurementRecorded>();

        foreach (var row in rows)
        {
            // Signal code là thứ quyết định. Một kết quả đã đánh giá là một signal khác với curve mà
            // nó bắt nguồn - Formation/CapacityResult khác với Formation/Capacity - nên whitelist có
            // thể nêu tên cái này mà không bao giờ chấp nhận cái kia, và nó vẫn tiếp tục như vậy sau
            // khi M7 gán unit id cho mọi raw reading.
            //
            // Unit id sau đó cũng bắt buộc phải có, vì một event chấm điểm một cell phải nói rõ là
            // CELL NÀO; một result signal đến mà không có unit id là dị dạng chứ không phải
            // telemetry, và nó được lưu lại nhưng không được announce thay vì announce về không ai cả.
            if (_publishedSignals.Includes(row.SignalCode)
                && !string.IsNullOrWhiteSpace(row.UnitId))
            {
                announced.Add(row.ToEvent());
            }
        }

        return announced;
    }

    private static async Task<HashSet<Guid>> ClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MeasurementRow[] rows,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ClaimSql, connection, transaction);
        AddArray(command, "source_event_ids", NpgsqlDbType.Uuid, rows.Select(row => row.SourceEventId).ToArray());
        AddArray(command, "site_ids", NpgsqlDbType.Text, rows.Select(row => row.SiteId).ToArray());
        AddArray(command, "natural_keys", NpgsqlDbType.Text, rows.Select(row => row.NaturalKey).ToArray());
        AddArray(command, "first_seen_ats", NpgsqlDbType.TimestampTz, rows.Select(row => row.RecordedAt).ToArray());

        var claimed = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(reader.GetGuid(0));
        }

        return claimed;
    }

    private static async Task StoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MeasurementRow[] rows,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(StoreSql, connection, transaction);
        AddArray(command, "source_event_ids", NpgsqlDbType.Uuid, rows.Select(row => row.SourceEventId).ToArray());
        AddArray(command, "site_ids", NpgsqlDbType.Text, rows.Select(row => row.SiteId).ToArray());
        AddArray(command, "equipment_ids", NpgsqlDbType.Text, rows.Select(row => row.EquipmentId).ToArray());
        AddArray(command, "unit_ids", NpgsqlDbType.Text, rows.Select(row => row.UnitId).ToArray());
        AddArray(command, "step_codes", NpgsqlDbType.Text, rows.Select(row => row.StepCode).ToArray());
        AddArray(command, "signal_codes", NpgsqlDbType.Text, rows.Select(row => row.SignalCode).ToArray());
        AddArray(command, "device_timestamps", NpgsqlDbType.TimestampTz, rows.Select(row => row.DeviceTimestamp).ToArray());
        AddArray(command, "gateway_timestamps", NpgsqlDbType.TimestampTz, rows.Select(row => row.GatewayTimestamp).ToArray());
        AddArray(command, "recorded_ats", NpgsqlDbType.TimestampTz, rows.Select(row => row.RecordedAt).ToArray());
        AddArray(command, "clock_qualities", NpgsqlDbType.Text, rows.Select(row => row.ClockQuality.ToColumnValue()).ToArray());
        AddArray(command, "value_kinds", NpgsqlDbType.Text, rows.Select(row => row.ValueKind).ToArray());
        AddArray(command, "real_values", NpgsqlDbType.Double, rows.Select(row => row.RealValue).ToArray());
        AddArray(command, "integer_values", NpgsqlDbType.Bigint, rows.Select(row => row.IntegerValue).ToArray());
        AddArray(command, "boolean_values", NpgsqlDbType.Boolean, rows.Select(row => row.BooleanValue).ToArray());
        AddArray(command, "text_values", NpgsqlDbType.Text, rows.Select(row => row.TextValue).ToArray());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddArray<T>(NpgsqlCommand command, string name, NpgsqlDbType elementType, T[] values) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.Array | elementType, values);
}
