using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.Kernel.EventSourcing;

namespace Nvm.Projections;

public interface IGlobalEventFeed
{
    Task<IReadOnlyList<StoredStreamEvent>> ReadAsync(
        string siteId, long afterGlobalSequence, int limit, CancellationToken cancellationToken);
}

/// <summary>Reads committed SQL Server facts in sequence order for one site.</summary>
public sealed class SqlGlobalEventFeed(string connectionString) : IGlobalEventFeed
{
    /// <summary>Resolves a committed broker event to its authoritative stream/version without scanning history.</summary>
    public async Task<StoredStreamEvent?> FindAsync(string siteId, Guid eventId, CancellationToken cancellationToken)
    {
        ProjectionIdentity.ValidateSite(siteId);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT GlobalSequence, SiteId, StreamId, Version, SourceEventId, EventType,
                SchemaVersion, PayloadJson, MetadataJson, OccurredAt, RecordedAt, CloudEventJson
            FROM es.Events WHERE SiteId = @site AND SourceEventId = @event;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = eventId;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredStreamEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetGuid(4), reader.GetString(5), reader.GetInt32(6),
                reader.GetString(7), reader.GetString(8),
                await reader.GetFieldValueAsync<DateTimeOffset>(9, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(10, cancellationToken).ConfigureAwait(false))
            { EnvelopeJson = reader.GetString(11) }
            : null;
    }

    public async Task<IReadOnlyList<StoredStreamEvent>> ReadAsync(
        string siteId, long afterGlobalSequence, int limit, CancellationToken cancellationToken)
    {
        ProjectionIdentity.ValidateSite(siteId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterGlobalSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (@limit) GlobalSequence, SiteId, StreamId, Version,
                SourceEventId, EventType, SchemaVersion, PayloadJson, MetadataJson,
                OccurredAt, RecordedAt, CloudEventJson
            FROM es.Events WITH (READCOMMITTEDLOCK)
            WHERE SiteId = @site AND GlobalSequence > @after
            ORDER BY GlobalSequence;
            """, connection);
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@after", SqlDbType.BigInt).Value = afterGlobalSequence;
        var events = new List<StoredStreamEvent>(limit);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new StoredStreamEvent(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetGuid(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
                reader.GetString(8),
                await reader.GetFieldValueAsync<DateTimeOffset>(9, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(10, cancellationToken).ConfigureAwait(false))
            { EnvelopeJson = reader.GetString(11) });
        }
        return events;
    }
}

internal static class ProjectionIdentity
{
    internal static void ValidateSite(string siteId)
    {
        if (siteId is not { Length: 3 } || siteId.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c)))
        { throw new ArgumentException("Site ID must be three uppercase ASCII letters/digits.", nameof(siteId)); }
    }
}
