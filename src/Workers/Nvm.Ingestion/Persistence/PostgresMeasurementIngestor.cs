using Npgsql;
using NpgsqlTypes;
using Nvm.Contracts.Events.Quality;
using Nvm.Ingestion.FileDrop;
using Nvm.Ingestion.Publishing;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.Persistence;

/// <summary>Uses PostgreSQL uniqueness as the authority for device-level idempotency.</summary>
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

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;
    private readonly IngestionMetrics _metrics;
    private readonly IMeasurementEventPublisher _publisher;
    private readonly PublishedSignals _publishedSignals;
    private readonly TimeSpan _clockDriftThreshold;

    /// <summary>Creates the transaction boundary used by HTTP and file drop alike.</summary>
    /// <param name="dataSource">Connection source for the one transaction per batch.</param>
    /// <param name="timeProvider">Stamps <c>recorded_at</c> (K1).</param>
    /// <param name="metrics">Counters updated only after the transaction commits.</param>
    /// <param name="publisher">Where committed business facts go next. Null publishes nothing.</param>
    /// <param name="publishedSignals">Which signal codes are business facts (scope.md §5.5).</param>
    /// <param name="clockDriftThreshold">
    /// How far the device and gateway clocks may disagree before a reading is flagged. Defaults to
    /// <see cref="ClockQualityClassifier.DefaultThreshold"/>.
    /// </param>
    public PostgresMeasurementIngestor(
        NpgsqlDataSource dataSource,
        TimeProvider timeProvider,
        IngestionMetrics metrics,
        IMeasurementEventPublisher? publisher = null,
        PublishedSignals? publishedSignals = null,
        TimeSpan? clockDriftThreshold = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _publisher = publisher ?? NullMeasurementEventPublisher.Instance;
        _publishedSignals = publishedSignals ?? PublishedSignals.None;
        _clockDriftThreshold = clockDriftThreshold ?? ClockQualityClassifier.DefaultThreshold;
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

    // The one transaction both adapters commit through. Neither of them gets to decide what dedup
    // means; they only decide how to read.
    private async Task<IngestionResult> StoreAsync(
        Dictionary<Guid, MeasurementRow> distinct,
        int rawCount,
        CancellationToken cancellationToken)
    {
        if (rawCount == 0)
        {
            return new IngestionResult(0, 0);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var claimedIds = await ClaimAsync(connection, transaction, [.. distinct.Values], cancellationToken);
        var claimedRows = distinct.Values.Where(row => claimedIds.Contains(row.SourceEventId)).ToArray();

        if (claimedRows.Length > 0)
        {
            await StoreAsync(connection, transaction, claimedRows, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var drifted = claimedRows.Count(row => row.ClockQuality != ClockQuality.Good);
        var result = new IngestionResult(claimedRows.Length, rawCount - claimedRows.Length, drifted);
        _metrics.RecordCommitted(result);

        // After the commit, and outside it. A publish failure must leave the rows where they are:
        // this is the dual-write ADR-022 already measured at 18/200, and M2 counts it rather than
        // pretending the outbox that closes it (M6) is already here.
        var announced = Announce(claimedRows);

        if (announced.Count == 0)
        {
            return result;
        }

        var failures = await _publisher.PublishAsync(announced, cancellationToken);
        _metrics.RecordPublishFailures(failures);

        return result with { PublishFailures = failures };
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
            if (_publishedSignals.Includes(row.SignalCode))
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
