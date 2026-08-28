using Npgsql;
using NpgsqlTypes;
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
                @value_kinds::text[],
                @real_values::float8[],
                @integer_values::bigint[],
                @boolean_values::boolean[],
                @text_values::text[])
            AS input(
                source_event_id, site_id, equipment_id, unit_id, step_code, signal_code,
                device_timestamp, gateway_timestamp, recorded_at, value_kind,
                real_value, integer_value, boolean_value, text_value)
        )
        INSERT INTO ts.telemetry_measurement (
            source_event_id, site_id, equipment_id, unit_id, step_code, signal_code,
            device_timestamp, gateway_timestamp, recorded_at, value_kind,
            real_value, integer_value, boolean_value, text_value)
        SELECT
            source_event_id, site_id, equipment_id, unit_id, step_code, signal_code,
            device_timestamp, gateway_timestamp, recorded_at, value_kind,
            real_value, integer_value, boolean_value, text_value
        FROM incoming;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;
    private readonly IngestionMetrics _metrics;

    /// <summary>Creates the transaction boundary used by HTTP and, later, file drop.</summary>
    public PostgresMeasurementIngestor(
        NpgsqlDataSource dataSource,
        TimeProvider timeProvider,
        IngestionMetrics metrics)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
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
                var row = MeasurementRow.From(message, reading, recordedAt);
                distinct.TryAdd(row.SourceEventId, row);
            }
        }

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

        var result = new IngestionResult(claimedRows.Length, rawCount - claimedRows.Length);
        _metrics.RecordCommitted(result);
        return result;
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
