using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Nvm.Kernel.EventSourcing;

namespace Nvm.Projections;

/// <summary>Durable incremental delivery; bus acknowledgement follows EnqueueAsync commit.</summary>
public sealed class ProductionUnitProjectionInbox(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Stores the immutable source fact, or verifies an exact replay of a prior delivery.</summary>
    public async Task EnqueueAsync(StoredStreamEvent fact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ProjectionIdentity.ValidateSite(fact.SiteId);
        if (fact.SourceEventId == Guid.Empty || fact.Version <= 0 || fact.GlobalSequence <= 0)
        { throw new ArgumentException("Stored event identity/version is invalid.", nameof(fact)); }
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO rm.unit_projection_inbox
                (site_id, source_event_id, stream_id, stream_version, global_sequence, fact)
            VALUES (@site, @event, @stream, @version, @sequence, @fact)
            ON CONFLICT (site_id, source_event_id) DO NOTHING;
            SELECT fact = @fact FROM rm.unit_projection_inbox
            WHERE site_id = @site AND source_event_id = @event;
            """, connection, transaction);
        insert.Parameters.AddWithValue("site", fact.SiteId);
        insert.Parameters.AddWithValue("event", fact.SourceEventId);
        insert.Parameters.AddWithValue("stream", fact.StreamId);
        insert.Parameters.AddWithValue("version", fact.Version);
        insert.Parameters.AddWithValue("sequence", fact.GlobalSequence);
        insert.Parameters.Add("fact", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(fact, Json);
        if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        { throw new InvalidDataException("Duplicate event ID carries a different source fact."); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies ready stream versions and acknowledges them atomically with their checkpoint.</summary>
    public async Task<int> DispatchAsync(string siteId, int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ProjectionIdentity.ValidateSite(siteId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 5000);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var generation = await ProductionUnitProjection.LockCheckpointAsync(connection, transaction,
            siteId, cancellationToken).ConfigureAwait(false);
        var facts = new List<StoredStreamEvent>();
        await using (var pending = new NpgsqlCommand("""
            SELECT i.fact::text
            FROM rm.unit_projection_inbox i
            LEFT JOIN rm.unit_current u ON u.site_id = i.site_id AND u.serial_number = i.stream_id
            LEFT JOIN rm.unit_quality q ON q.site_id = i.site_id AND 'quality:' || q.serial_number = i.stream_id
            WHERE i.site_id = @site AND NOT i.applied
                AND i.stream_version <= coalesce(u.stream_version, q.stream_version, 0) + 1
            ORDER BY i.global_sequence LIMIT @limit
            FOR UPDATE OF i;
            """, connection, transaction))
        {
            pending.Parameters.AddWithValue("site", siteId);
            pending.Parameters.AddWithValue("limit", limit);
            await using var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                facts.Add(JsonSerializer.Deserialize<StoredStreamEvent>(reader.GetString(0), Json)
                    ?? throw new InvalidDataException("Stored projection fact is null."));
            }
        }
        if (facts.Count > 0)
        {
            await ProductionUnitProjection.ApplyBatchInTransactionAsync(connection, transaction,
                siteId, facts, generation, cancellationToken).ConfigureAwait(false);
            await using var acknowledge = new NpgsqlCommand("""
                UPDATE rm.unit_projection_inbox SET applied = true
                WHERE site_id = @site AND source_event_id = ANY(@events);
                """, connection, transaction);
            acknowledge.Parameters.AddWithValue("site", siteId);
            acknowledge.Parameters.AddWithValue("events", facts.Select(f => f.SourceEventId).ToArray());
            await acknowledge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return facts.Count;
    }
}
