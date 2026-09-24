using System.Data;
using Microsoft.Data.SqlClient;

namespace Nvm.EventStore;

/// <summary>A durable event awaiting transport. EventId must be the transport message identity on every retry.</summary>
public sealed record OutboxEvent(
    Guid EventId, string SiteId, string StreamId, long Version, string EventType,
    int SchemaVersion, string PayloadJson, string MetadataJson,
    DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, string CloudEventJson);

/// <summary>Transport adapter; implementations must publish using the unchanged EventId and honor cancellation.</summary>
public interface IEventOutboxPublisher
{
    Task PublishAsync(OutboxEvent message, CancellationToken cancellationToken);
}

public sealed record OutboxDispatchResult(int Claimed, int Published, int Failed);

/// <summary>Claims SQL Server outbox rows, publishes them at least once, and records acknowledgement.</summary>
public sealed class SqlEventOutboxDispatcher
{
    private readonly SqlEventStoreOptions _options;
    private readonly IEventOutboxPublisher _publisher;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lease;

    public SqlEventOutboxDispatcher(SqlEventStoreOptions options, IEventOutboxPublisher publisher,
        TimeProvider clock, TimeSpan? lease = null)
    {
        _options = options;
        _publisher = publisher;
        _clock = clock;
        _lease = lease ?? TimeSpan.FromMinutes(2);
        if (_lease <= TimeSpan.Zero)
        { throw new ArgumentOutOfRangeException(nameof(lease)); }
    }

    public async Task<OutboxDispatchResult> DispatchOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 100)
        { throw new ArgumentOutOfRangeException(nameof(batchSize)); }
        var claimedCount = 0;
        var published = 0;
        var failed = 0;
        for (var i = 0; i < batchSize; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Acquire only the event we can send now; queued batch members must not
            // spend their lease waiting behind another publisher's network call.
            var claimed = await ClaimAsync(1, cancellationToken).ConfigureAwait(false);
            if (claimed.Count == 0)
            { break; }
            var claim = claimed[0];
            claimedCount++;
            using var budget = new CancellationTokenSource(TimeSpan.FromTicks(_lease.Ticks / 2), _clock);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
            try
            {
                await _publisher.PublishAsync(claim.Message, attempt.Token).ConfigureAwait(false);
                await AcknowledgeAsync(claim, cancellationToken).ConfigureAwait(false);
                published++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Leave the lease in place. Another process reclaims it when the lease expires.
                throw;
            }
            catch (Exception error)
            {
                await FailAsync(claim, error, cancellationToken).ConfigureAwait(false);
                failed++;
            }
        }
        return new OutboxDispatchResult(claimedCount, published, failed);
    }

    private async Task<List<ClaimedEvent>> ClaimAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var claimId = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Update the base table, not the projected CTE: SQL Server otherwise requires
        // broader UPDATE rights than the runtime's column grants. Lock candidate IDs
        // and update their mutable delivery fields in the same statement.
        using var command = new SqlCommand("""
            ;WITH pending AS (
                SELECT TOP (@batch) EventId
                FROM es.Outbox WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE DispatchedAt IS NULL AND NextAttemptAt <= @now
                ORDER BY NextAttemptAt, CreatedAt, EventId
            )
            UPDATE target
            SET Attempt = Attempt + 1, ClaimId = @claim,
                NextAttemptAt = @leaseUntil
            OUTPUT inserted.EventId, inserted.SiteId, inserted.StreamId, inserted.Version,
                inserted.EventType, inserted.SchemaVersion, inserted.PayloadJson,
                inserted.MetadataJson, inserted.OccurredAt, inserted.RecordedAt,
                inserted.Attempt, stored.CloudEventJson
            FROM es.Outbox AS target
            INNER JOIN pending ON target.EventId = pending.EventId
            INNER JOIN es.Events AS stored ON stored.SourceEventId = target.EventId;
            """, connection) { CommandTimeout = _options.CommandTimeoutSeconds };
        command.Parameters.Add("@batch", SqlDbType.Int).Value = batchSize;
        command.Parameters.Add("@now", SqlDbType.DateTimeOffset).Value = now;
        command.Parameters.Add("@claim", SqlDbType.UniqueIdentifier).Value = claimId;
        command.Parameters.Add("@leaseUntil", SqlDbType.DateTimeOffset).Value = now + _lease;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ClaimedEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ClaimedEvent(new OutboxEvent(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(9, cancellationToken).ConfigureAwait(false),
                reader.GetString(11)),
                claimId, reader.GetInt32(10)));
        }
        return result;
    }

    private async Task AcknowledgeAsync(ClaimedEvent claim, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            UPDATE es.Outbox SET DispatchedAt = @now, ClaimId = NULL, LastError = NULL
            WHERE EventId = @id AND ClaimId = @claim AND DispatchedAt IS NULL;
            """, connection) { CommandTimeout = _options.CommandTimeoutSeconds };
        command.Parameters.Add("@now", SqlDbType.DateTimeOffset).Value = _clock.GetUtcNow();
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = claim.Message.EventId;
        command.Parameters.Add("@claim", SqlDbType.UniqueIdentifier).Value = claim.ClaimId;
        // A zero-row update means another worker reclaimed an expired lease. The event
        // remains pending; a late publisher must never acknowledge the newer claim.
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FailAsync(ClaimedEvent claim, Exception error, CancellationToken cancellationToken)
    {
        var exponent = Math.Min(claim.Attempt - 1, 6);
        var delay = TimeSpan.FromSeconds(5 * (1 << exponent));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            UPDATE es.Outbox
            SET ClaimId = NULL, NextAttemptAt = @retryAt, LastError = @error
            WHERE EventId = @id AND ClaimId = @claim AND DispatchedAt IS NULL;
            """, connection) { CommandTimeout = _options.CommandTimeoutSeconds };
        command.Parameters.Add("@retryAt", SqlDbType.DateTimeOffset).Value = _clock.GetUtcNow() + delay;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 2000).Value = error.Message[..Math.Min(error.Message.Length, 2000)];
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = claim.Message.EventId;
        command.Parameters.Add("@claim", SqlDbType.UniqueIdentifier).Value = claim.ClaimId;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private sealed record ClaimedEvent(OutboxEvent Message, Guid ClaimId, int Attempt);
}
