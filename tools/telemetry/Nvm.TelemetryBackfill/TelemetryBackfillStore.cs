using System.Collections.Immutable;
using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace Nvm.TelemetryBackfill;

/// <summary>Stream các row đã generate qua binary COPY và atomically claim chỉ những measurement mới.</summary>
public sealed class TelemetryBackfillStore
{
    private const long Million = 1_000_000;

    // Nêu tên site của nó, giống mọi lần đọc khác của một table có site (K3). Một phiên bản trước của
    // file này lập luận rằng ADR-030 miễn trừ điều này, vì dedup key là global và không partition.
    // Điều đó nhầm lẫn hai thứ khác nhau: ADR-030 chi phối nơi UNIQUENESS được thực thi, còn K3 chi
    // phối cách một QUERY được viết. PRIMARY KEY toàn cục vẫn là thẩm quyền và vẫn làm đúng việc của
    // nó — đó chính xác là điều khiến việc thêm site filter trở nên an toàn. Nếu một claim từng tồn
    // tại dưới nhãn của một plant khác, query này giờ báo row đó là mới, COPY bên dưới va vào primary
    // key đó, và batch fail một cách rõ ràng. Không có filter thì cùng một collision đó âm thầm giải
    // quyết thành "đã thấy rồi", và một measurement mà lần chạy này được yêu cầu lưu sẽ không bao giờ
    // được lưu.
    private const string FindExistingSql = """
        SELECT claim.source_event_id
        FROM unnest(@site_ids::TEXT[], @source_event_ids::UUID[])
            AS incoming(site_id, source_event_id)
        JOIN ingest.processed_message AS claim
          ON claim.source_event_id = incoming.source_event_id
         AND claim.site_id = incoming.site_id;
        """;

    private const string CopyClaimsSql = """
        COPY ingest.processed_message (source_event_id, site_id, natural_key, first_seen_at)
        FROM STDIN (FORMAT BINARY)
        """;

    private const string CopyTelemetrySql = """
        COPY ts.telemetry_measurement (
            source_event_id, site_id, equipment_id, unit_id, step_code, signal_code,
            device_timestamp, gateway_timestamp, recorded_at, clock_quality, value_kind,
            real_value, integer_value, boolean_value, text_value)
        FROM STDIN (FORMAT BINARY)
        """;

    // Mọi lần đọc ts.telemetry_measurement đều nêu tên site của nó (K3). Ở đây site không phải một
    // permission check mà là một identity check, và nó là cái mạnh hơn trong hai loại: query này
    // chính là thứ quyết định một row đã claim có thực sự tồn tại hay không, và một match tìm thấy
    // dưới một site_id khác sẽ báo cáo một row là đã verify trong khi row mà lần chạy này muốn nói
    // đến lại đang thiếu.
    private const string VerifyExistingTelemetrySql = """
        WITH incoming AS (
            SELECT site_id, source_event_id, device_timestamp
            FROM unnest(
                @site_ids::TEXT[],
                @source_event_ids::UUID[],
                @device_timestamps::TIMESTAMPTZ[])
                AS input(site_id, source_event_id, device_timestamp)
        )
        SELECT count(telemetry.source_event_id)
        FROM incoming
        LEFT JOIN ts.telemetry_measurement AS telemetry
          ON telemetry.site_id = incoming.site_id
         AND telemetry.source_event_id = incoming.source_event_id
         AND telemetry.device_timestamp = incoming.device_timestamp
         AND telemetry.device_timestamp >= @min_device_timestamp
         AND telemetry.device_timestamp <= @max_device_timestamp;
        """;

    private readonly string _connectionString;

    /// <summary>Tạo một store trên database TimescaleDB đã migrate.</summary>
    public TelemetryBackfillStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>Copy một lazy row stream theo batch có giới hạn và verify cả hai table cuối cùng.</summary>
    public async Task<TelemetryBackfillResult> WriteAsync(
        IEnumerable<BackfillRow> rows,
        int batchSize,
        Action<TelemetryBackfillProgress>? report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var intervalStarted = TimeSpan.Zero;
        long attempted = 0;
        long inserted = 0;
        long duplicates = 0;
        long verifiedClaims = 0;
        long verifiedTelemetry = 0;
        long good = 0;
        long drifted = 0;
        long intervalInserted = 0;
        var claimCopyElapsed = TimeSpan.Zero;
        var telemetryCopyElapsed = TimeSpan.Zero;
        var intervalClaimCopyElapsed = TimeSpan.Zero;
        var intervalTelemetryCopyElapsed = TimeSpan.Zero;
        var signals = new Dictionary<string, long>(StringComparer.Ordinal);
        var batch = new List<BackfillRow>(Math.Min(batchSize, (int)Million));

        foreach (var row in rows)
        {
            batch.Add(row);
            signals[row.SignalCode] = signals.GetValueOrDefault(row.SignalCode) + 1;
            good += string.Equals(row.ClockQuality, "Good", StringComparison.Ordinal) ? 1 : 0;
            drifted += string.Equals(row.ClockQuality, "Drifted", StringComparison.Ordinal) ? 1 : 0;

            var untilMillion = (int)(Million - (attempted % Million));
            var target = Math.Min(batchSize, untilMillion);

            if (batch.Count < target)
            {
                continue;
            }

            var result = await WriteBatchAsync(connection, batch, cancellationToken);
            attempted += result.Attempted;
            inserted += result.Inserted;
            intervalInserted += result.Inserted;
            claimCopyElapsed += result.ClaimCopyElapsed;
            telemetryCopyElapsed += result.TelemetryCopyElapsed;
            intervalClaimCopyElapsed += result.ClaimCopyElapsed;
            intervalTelemetryCopyElapsed += result.TelemetryCopyElapsed;
            duplicates += result.Attempted - result.Inserted;
            verifiedClaims += result.VerifiedClaims;
            verifiedTelemetry += result.VerifiedTelemetry;
            batch.Clear();

            if (attempted % Million == 0)
            {
                var elapsed = stopwatch.Elapsed - intervalStarted;
                report?.Invoke(
                    new TelemetryBackfillProgress(
                        attempted,
                        Million,
                        intervalInserted,
                        intervalClaimCopyElapsed,
                        intervalTelemetryCopyElapsed,
                        elapsed,
                        true));
                intervalStarted = stopwatch.Elapsed;
                intervalInserted = 0;
                intervalClaimCopyElapsed = TimeSpan.Zero;
                intervalTelemetryCopyElapsed = TimeSpan.Zero;
            }
        }

        if (batch.Count > 0)
        {
            var result = await WriteBatchAsync(connection, batch, cancellationToken);
            attempted += result.Attempted;
            inserted += result.Inserted;
            intervalInserted += result.Inserted;
            claimCopyElapsed += result.ClaimCopyElapsed;
            telemetryCopyElapsed += result.TelemetryCopyElapsed;
            intervalClaimCopyElapsed += result.ClaimCopyElapsed;
            intervalTelemetryCopyElapsed += result.TelemetryCopyElapsed;
            duplicates += result.Attempted - result.Inserted;
            verifiedClaims += result.VerifiedClaims;
            verifiedTelemetry += result.VerifiedTelemetry;
            var intervalRows = attempted % Million;
            report?.Invoke(
                new TelemetryBackfillProgress(
                    attempted,
                    intervalRows,
                    intervalInserted,
                    intervalClaimCopyElapsed,
                    intervalTelemetryCopyElapsed,
                    stopwatch.Elapsed - intervalStarted,
                    false));
        }

        stopwatch.Stop();

        return new TelemetryBackfillResult(
            attempted,
            inserted,
            duplicates,
            verifiedClaims,
            verifiedTelemetry,
            good,
            drifted,
            signals.ToImmutableDictionary(StringComparer.Ordinal),
            claimCopyElapsed,
            telemetryCopyElapsed,
            stopwatch.Elapsed);
    }

    private static async Task<BatchResult> WriteBatchAsync(
        NpgsqlConnection connection,
        List<BackfillRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Select(row => row.SourceEventId).ToHashSet().Count != rows.Count)
        {
            throw new InvalidOperationException("The generator repeated a source event ID inside one batch.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await FindExistingAsync(connection, transaction, rows, cancellationToken);
        var existingRows = rows.Where(row => existing.Contains(row.SourceEventId)).ToList();
        var claimedRows = rows.Where(row => !existing.Contains(row.SourceEventId)).ToList();
        var claimCopyElapsed = TimeSpan.Zero;
        var telemetryCopyElapsed = TimeSpan.Zero;
        var verifiedExistingTelemetry = await VerifyExistingTelemetryAsync(
            connection,
            transaction,
            existingRows,
            cancellationToken);

        if (claimedRows.Count > 0)
        {
            var phase = Stopwatch.StartNew();
            var copiedClaims = await CopyClaimsAsync(connection, claimedRows, cancellationToken);
            phase.Stop();
            claimCopyElapsed = phase.Elapsed;

            phase.Restart();
            var copiedTelemetry = await CopyTelemetryAsync(connection, claimedRows, cancellationToken);
            phase.Stop();
            telemetryCopyElapsed = phase.Elapsed;

            if (copiedClaims != claimedRows.Count || copiedTelemetry != claimedRows.Count)
            {
                throw new InvalidOperationException(
                    $"COPY expected {claimedRows.Count} new rows, copied claims={copiedClaims}, "
                    + $"telemetry={copiedTelemetry}.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new BatchResult(
            rows.Count,
            claimedRows.Count,
            existingRows.Count + claimedRows.Count,
            verifiedExistingTelemetry + claimedRows.Count,
            claimCopyElapsed,
            telemetryCopyElapsed);
    }

    private static async Task<HashSet<Guid>> FindExistingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<BackfillRow> rows,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FindExistingSql, connection, transaction);
        AddArray(
            command,
            "site_ids",
            NpgsqlDbType.Text,
            rows.Select(row => row.SiteId).ToArray());
        AddArray(
            command,
            "source_event_ids",
            NpgsqlDbType.Uuid,
            rows.Select(row => row.SourceEventId).ToArray());

        var existing = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            existing.Add(reader.GetGuid(0));
        }

        return existing;
    }

    private static async Task<long> VerifyExistingTelemetryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<BackfillRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        await using var command = new NpgsqlCommand(VerifyExistingTelemetrySql, connection, transaction);
        AddArray(
            command,
            "site_ids",
            NpgsqlDbType.Text,
            rows.Select(row => row.SiteId).ToArray());
        AddArray(
            command,
            "source_event_ids",
            NpgsqlDbType.Uuid,
            rows.Select(row => row.SourceEventId).ToArray());
        AddArray(
            command,
            "device_timestamps",
            NpgsqlDbType.TimestampTz,
            rows.Select(row => row.DeviceTimestamp).ToArray());
        command.Parameters.AddWithValue(
            "min_device_timestamp",
            NpgsqlDbType.TimestampTz,
            rows.Min(row => row.DeviceTimestamp));
        command.Parameters.AddWithValue(
            "max_device_timestamp",
            NpgsqlDbType.TimestampTz,
            rows.Max(row => row.DeviceTimestamp));

        var verified = (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Existing telemetry verification returned no count."));

        if (verified != rows.Count)
        {
            throw new InvalidOperationException(
                $"Found {rows.Count} existing claims but only {verified} exact telemetry rows.");
        }

        return verified;
    }

    private static async Task<long> CopyClaimsAsync(
        NpgsqlConnection connection,
        List<BackfillRow> rows,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(CopyClaimsSql, cancellationToken);

        foreach (var row in rows)
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(row.SourceEventId, NpgsqlDbType.Uuid, cancellationToken);
            await importer.WriteAsync(row.SiteId, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.NaturalKey, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.RecordedAt, NpgsqlDbType.TimestampTz, cancellationToken);
        }

        return checked((long)await importer.CompleteAsync(cancellationToken));
    }

    private static async Task<long> CopyTelemetryAsync(
        NpgsqlConnection connection,
        List<BackfillRow> rows,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(CopyTelemetrySql, cancellationToken);

        foreach (var row in rows)
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(row.SourceEventId, NpgsqlDbType.Uuid, cancellationToken);
            await importer.WriteAsync(row.SiteId, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.EquipmentId, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(importer, row.UnitId, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.StepCode, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.SignalCode, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.DeviceTimestamp, NpgsqlDbType.TimestampTz, cancellationToken);
            await importer.WriteAsync(row.GatewayTimestamp, NpgsqlDbType.TimestampTz, cancellationToken);
            await importer.WriteAsync(row.RecordedAt, NpgsqlDbType.TimestampTz, cancellationToken);
            await importer.WriteAsync(row.ClockQuality, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.ValueKind, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableAsync(importer, row.RealValue, NpgsqlDbType.Double, cancellationToken);
            await WriteNullableAsync(importer, row.IntegerValue, NpgsqlDbType.Bigint, cancellationToken);
            await WriteNullableAsync(importer, row.BooleanValue, NpgsqlDbType.Boolean, cancellationToken);
            await WriteNullableAsync(importer, row.TextValue, NpgsqlDbType.Text, cancellationToken);
        }

        return checked((long)await importer.CompleteAsync(cancellationToken));
    }

    private static void AddArray<T>(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType elementType,
        T[] values) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.Array | elementType, values);

    private static async ValueTask WriteNullableAsync<T>(
        NpgsqlBinaryImporter importer,
        T? value,
        NpgsqlDbType type,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await importer.WriteNullAsync(cancellationToken);
        }
        else
        {
            await importer.WriteAsync(value, type, cancellationToken);
        }
    }

    private sealed record BatchResult(
        long Attempted,
        long Inserted,
        long VerifiedClaims,
        long VerifiedTelemetry,
        TimeSpan ClaimCopyElapsed,
        TimeSpan TelemetryCopyElapsed);
}

/// <summary>Một checkpoint đúng-một-triệu, hoặc khoảng cuối cùng còn dở dang.</summary>
public sealed record TelemetryBackfillProgress(
    long AttemptedThrough,
    long IntervalRows,
    long IntervalInserted,
    TimeSpan ClaimCopyElapsed,
    TimeSpan TelemetryCopyElapsed,
    TimeSpan Elapsed,
    bool IsWholeMillion)
{
    /// <summary>Số row đã generate được xử lý mỗi giây trong khoảng này.</summary>
    public double RowsPerSecond => IntervalRows / Elapsed.TotalSeconds;

    /// <summary>Số claim toàn cục mới được copy mỗi giây trong khoảng này.</summary>
    public double ClaimRowsPerSecond => Rate(IntervalInserted, ClaimCopyElapsed);

    /// <summary>Số telemetry row mới được copy mỗi giây trong khoảng này.</summary>
    public double TelemetryRowsPerSecond => Rate(IntervalInserted, TelemetryCopyElapsed);

    private static double Rate(long rows, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? rows / elapsed.TotalSeconds : 0;
}

/// <summary>Tổng số có thể audit cho một lần gọi.</summary>
public sealed record TelemetryBackfillResult(
    long Attempted,
    long Inserted,
    long Duplicates,
    long VerifiedClaims,
    long VerifiedTelemetry,
    long Good,
    long Drifted,
    ImmutableDictionary<string, long> SignalCounts,
    TimeSpan ClaimCopyElapsed,
    TimeSpan TelemetryCopyElapsed,
    TimeSpan Elapsed)
{
    /// <summary>Số row đã generate được xử lý mỗi giây trong suốt lần chạy.</summary>
    public double RowsPerSecond => Attempted / Elapsed.TotalSeconds;

    /// <summary>Số claim toàn cục mới được copy mỗi giây trong suốt lần chạy.</summary>
    public double ClaimRowsPerSecond => Rate(Inserted, ClaimCopyElapsed);

    /// <summary>Số telemetry row mới được copy mỗi giây trong suốt lần chạy.</summary>
    public double TelemetryRowsPerSecond => Rate(Inserted, TelemetryCopyElapsed);

    private static double Rate(long rows, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? rows / elapsed.TotalSeconds : 0;
}
