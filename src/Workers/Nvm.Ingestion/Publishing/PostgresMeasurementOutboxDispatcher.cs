using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Nvm.Contracts.Events.Quality;

namespace Nvm.Ingestion.Publishing;

/// <summary>Retries durable PostgreSQL measurement intents until the bus accepts them.</summary>
/// <remarks>
/// The broker and PostgreSQL cannot commit atomically. A crash after broker acceptance but before
/// published_at is committed can deliver the same EventId again; consumers must deduplicate it.
/// FOR UPDATE SKIP LOCKED lets multiple dispatcher instances drain distinct rows safely.
/// </remarks>
public sealed class PostgresMeasurementOutboxDispatcher : BackgroundService
{
    private const string ClaimSql = """
        SELECT event_id, payload::text, attempt
        FROM ingest.measurement_outbox
        WHERE published_at IS NULL AND next_attempt_at <= @now
        ORDER BY next_attempt_at, event_id
        LIMIT 1
        FOR UPDATE SKIP LOCKED;
        """;

    private const string CompleteSql = """
        UPDATE ingest.measurement_outbox
        SET published_at = @now, attempt = attempt + 1
        WHERE event_id = @event_id AND published_at IS NULL;
        """;

    private const string RetrySql = """
        UPDATE ingest.measurement_outbox
        SET attempt = attempt + 1, next_attempt_at = @next_attempt_at
        WHERE event_id = @event_id AND published_at IS NULL;
        """;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;
    private readonly IMeasurementEventPublisher _publisher;
    private readonly ILogger<PostgresMeasurementOutboxDispatcher> _logger;
    private readonly IngestionMetrics? _metrics;

    public PostgresMeasurementOutboxDispatcher(
        NpgsqlDataSource dataSource,
        TimeProvider timeProvider,
        IMeasurementEventPublisher publisher,
        ILogger<PostgresMeasurementOutboxDispatcher> logger,
        IngestionMetrics? metrics = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
    }

    /// <summary>Processes at most one due row; useful for controlled replay and deterministic tests.</summary>
    public async Task<bool> DispatchOneAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        Guid eventId;
        string payload;
        int attempt;

        await using (var claim = new NpgsqlCommand(ClaimSql, connection, transaction))
        {
            claim.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            eventId = reader.GetGuid(0);
            payload = reader.GetString(1);
            attempt = reader.GetInt32(2);
        }

        var measurement = JsonSerializer.Deserialize<MeasurementRecorded>(payload)
            ?? throw new JsonException($"Empty measurement outbox payload for {eventId}.");
        if (measurement.EventId != eventId)
        {
            throw new InvalidDataException($"Measurement outbox identity mismatch for {eventId}.");
        }

        var failure = 0;
        try
        {
            failure = await _publisher.PublishAsync([measurement], cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Measurement outbox publish threw for {EventId}", eventId);
            failure = 1;
        }

        if (failure == 0)
        {
            await using var complete = new NpgsqlCommand(CompleteSql, connection, transaction);
            complete.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, _timeProvider.GetUtcNow());
            complete.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
            await complete.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            var nextAttempt = _timeProvider.GetUtcNow() + RetryDelay(attempt);
            await using var retry = new NpgsqlCommand(RetrySql, connection, transaction);
            retry.Parameters.AddWithValue("next_attempt_at", NpgsqlDbType.TimestampTz, nextAttempt);
            retry.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
            await retry.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogWarning("Measurement outbox publish failed for {EventId}; retry at {NextAttemptAt}",
                eventId, nextAttempt);
        }

        await transaction.CommitAsync(cancellationToken);
        _metrics?.RecordPublishOutcome(1, failure);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await DispatchOneAsync(stoppingToken))
                {
                    await Task.Delay(IdleDelay, _timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Measurement outbox dispatcher failed; it will retry");
                await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken);
            }
        }
    }

    private static TimeSpan RetryDelay(int priorAttempts) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(priorAttempts, 8))));
}
