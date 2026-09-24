using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Kernel.EventSourcing;

namespace Nvm.EventStore;

/// <summary>SQL Server append-only event store. Writes share the durable command's SQL transaction.</summary>
public sealed class SqlEventStore : IEventStore
{
    private readonly SqlCommandSession _session;
    private readonly SqlEventStoreOptions _options;
    private readonly EventUpcasterChain _upcasters;

    public SqlEventStore(SqlCommandSession session, SqlEventStoreOptions options, EventUpcasterChain upcasters)
    {
        _session = session;
        _options = options;
        _upcasters = upcasters;
    }

    public async Task<long> AppendAsync(string siteId, string streamId, string streamType, long expectedVersion,
        ImmutableArray<NewStreamEvent> events, CancellationToken cancellationToken)
    {
        ValidateIdentity(siteId, streamId);
        if (string.IsNullOrWhiteSpace(streamType) || streamType.Length > 100)
        { throw new ArgumentException("Invalid stream type.", nameof(streamType)); }
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (events.IsDefaultOrEmpty)
        { throw new ArgumentException("Append requires at least one event.", nameof(events)); }
        if (!string.Equals(_session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Event site does not match the active command transaction."); }
        ValidateEvents(events);

        // The key-range lock also serializes the first writer of a stream that does not exist yet.
        using var read = Command("""
            SELECT StreamType, Version FROM es.Streams WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND StreamId = @stream;
            """);
        AddIdentity(read, siteId, streamId);
        string? actualType = null;
        long actualVersion = 0;
        using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                actualType = await reader.GetFieldValueAsync<string>(0, cancellationToken).ConfigureAwait(false);
                actualVersion = await reader.GetFieldValueAsync<long>(1, cancellationToken).ConfigureAwait(false);
            }
        }
        if (actualType is not null && !string.Equals(actualType, streamType, StringComparison.Ordinal))
        { throw new InvalidOperationException("Stream type does not match the existing stream."); }

        var nextVersion = checked(expectedVersion + events.Length);
        if (actualVersion != expectedVersion)
        {
            if (actualVersion >= nextVersion
                && await IsExactReplayAsync(siteId, streamId, expectedVersion, events, cancellationToken).ConfigureAwait(false))
            { return nextVersion; }
            throw new EventConcurrencyException(siteId, streamId, expectedVersion, actualVersion);
        }

        if (actualType is null)
        {
            using var create = Command("""
                INSERT INTO es.Streams (SiteId, StreamId, StreamType, Version, CreatedAt)
                VALUES (@site, @stream, @type, 0, @at);
                """);
            AddIdentity(create, siteId, streamId);
            create.Parameters.Add("@type", SqlDbType.VarChar, 100).Value = streamType;
            create.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = events[0].RecordedAt;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A stream header is mutable metadata. Event rows themselves are never updated or deleted.
        using (var advance = Command("""
            UPDATE es.Streams SET Version = @next
            WHERE SiteId = @site AND StreamId = @stream AND Version = @expected;
            """))
        {
            AddIdentity(advance, siteId, streamId);
            advance.Parameters.Add("@next", SqlDbType.BigInt).Value = nextVersion;
            advance.Parameters.Add("@expected", SqlDbType.BigInt).Value = expectedVersion;
            if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            { throw new EventConcurrencyException(siteId, streamId, expectedVersion, actualVersion); }
        }

        for (var i = 0; i < events.Length; i++)
        {
            var item = events[i];
            var envelopeJson = StoredCloudEventEnvelope.Serialize(siteId, item, _options.SourceApplicationName);
            using var insert = Command("""
                INSERT INTO es.Events
                    (SiteId, StreamId, Version, SourceEventId, EventType, SchemaVersion,
                     PayloadJson, MetadataJson, CloudEventJson, OccurredAt, RecordedAt)
                VALUES
                    (@site, @stream, @version, @source, @type, @schema,
                     @payload, @metadata, @envelope, @occurred, @recorded);
                """);
            AddIdentity(insert, siteId, streamId);
            insert.Parameters.Add("@version", SqlDbType.BigInt).Value = expectedVersion + i + 1;
            insert.Parameters.Add("@source", SqlDbType.UniqueIdentifier).Value = item.SourceEventId;
            insert.Parameters.Add("@type", SqlDbType.VarChar, 200).Value = item.EventType;
            insert.Parameters.Add("@schema", SqlDbType.Int).Value = item.SchemaVersion;
            insert.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = item.PayloadJson;
            insert.Parameters.Add("@metadata", SqlDbType.NVarChar, -1).Value = item.MetadataJson;
            insert.Parameters.Add("@envelope", SqlDbType.NVarChar, -1).Value = envelopeJson;
            insert.Parameters.Add("@occurred", SqlDbType.DateTimeOffset).Value = item.OccurredAt;
            insert.Parameters.Add("@recorded", SqlDbType.DateTimeOffset).Value = item.RecordedAt;
            try
            { await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
            catch (SqlException exception) when (exception.Number is 2601 or 2627)
            { throw new EventIdentityConflictException(item.SourceEventId); }

            // The durable publish decision must commit with the event row. A command rollback
            // removes both; a broker outage leaves this row available for the dispatcher.
            using var enqueue = Command("""
                INSERT INTO es.Outbox
                    (EventId, SiteId, StreamId, Version, EventType, SchemaVersion,
                     PayloadJson, MetadataJson, OccurredAt, RecordedAt, CreatedAt, NextAttemptAt)
                VALUES
                    (@id, @site, @stream, @version, @type, @schema,
                     @payload, @metadata, @occurred, @recorded, @recorded, @recorded);
                """);
            AddIdentity(enqueue, siteId, streamId);
            enqueue.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = item.SourceEventId;
            enqueue.Parameters.Add("@version", SqlDbType.BigInt).Value = expectedVersion + i + 1;
            enqueue.Parameters.Add("@type", SqlDbType.VarChar, 200).Value = item.EventType;
            enqueue.Parameters.Add("@schema", SqlDbType.Int).Value = item.SchemaVersion;
            enqueue.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = item.PayloadJson;
            enqueue.Parameters.Add("@metadata", SqlDbType.NVarChar, -1).Value = item.MetadataJson;
            enqueue.Parameters.Add("@occurred", SqlDbType.DateTimeOffset).Value = item.OccurredAt;
            enqueue.Parameters.Add("@recorded", SqlDbType.DateTimeOffset).Value = item.RecordedAt;
            await enqueue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return nextVersion;
    }

    public async Task<EventStream?> ReadStreamAsync(string siteId, string streamId, CancellationToken cancellationToken)
    {
        ValidateIdentity(siteId, streamId);
        await using var connection = await OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT s.StreamType, s.Version,
                   e.GlobalSequence, e.Version, e.SourceEventId, e.EventType, e.SchemaVersion,
                   e.PayloadJson, e.MetadataJson, e.OccurredAt, e.RecordedAt, e.CloudEventJson
            FROM es.Streams AS s WITH (HOLDLOCK)
            LEFT JOIN es.Events AS e ON e.SiteId = s.SiteId AND e.StreamId = s.StreamId
            WHERE s.SiteId = @site AND s.StreamId = @stream
            ORDER BY e.Version;
            """, connection) { CommandTimeout = _options.CommandTimeoutSeconds };
        AddIdentity(command, siteId, streamId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = false;
        string streamType = "";
        long streamVersion = 0;
        var events = ImmutableArray.CreateBuilder<StoredStreamEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!found)
            {
                streamType = await reader.GetFieldValueAsync<string>(0, cancellationToken).ConfigureAwait(false);
                streamVersion = await reader.GetFieldValueAsync<long>(1, cancellationToken).ConfigureAwait(false);
                found = true;
            }
            if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false))
            { continue; }
            var eventType = await reader.GetFieldValueAsync<string>(5, cancellationToken).ConfigureAwait(false);
            var schemaVersion = await reader.GetFieldValueAsync<int>(6, cancellationToken).ConfigureAwait(false);
            var payload = await reader.GetFieldValueAsync<string>(7, cancellationToken).ConfigureAwait(false);
            var upgraded = _upcasters.Upcast(eventType, schemaVersion, payload);
            events.Add(new StoredStreamEvent(
                await reader.GetFieldValueAsync<long>(2, cancellationToken).ConfigureAwait(false),
                siteId, streamId,
                await reader.GetFieldValueAsync<long>(3, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<Guid>(4, cancellationToken).ConfigureAwait(false),
                eventType, upgraded.Version, upgraded.PayloadJson,
                await reader.GetFieldValueAsync<string>(8, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(9, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(10, cancellationToken).ConfigureAwait(false))
            {
                EnvelopeJson = await reader.GetFieldValueAsync<string>(11, cancellationToken).ConfigureAwait(false),
            });
        }
        return found ? new EventStream(siteId, streamId, streamType, streamVersion, events.ToImmutable()) : null;
    }

    public async Task<EventSnapshot?> ReadSnapshotAsync(string siteId, string streamId, CancellationToken cancellationToken)
    {
        ValidateIdentity(siteId, streamId);
        await using var connection = await OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (1) Version, StateJson, RecordedAt FROM es.Snapshots
            WHERE SiteId = @site AND StreamId = @stream ORDER BY Version DESC;
            """, connection) { CommandTimeout = _options.CommandTimeoutSeconds };
        AddIdentity(command, siteId, streamId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new EventSnapshot(siteId, streamId,
            await reader.GetFieldValueAsync<long>(0, cancellationToken).ConfigureAwait(false),
            await reader.GetFieldValueAsync<string>(1, cancellationToken).ConfigureAwait(false),
            await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false));
    }

    public async Task SaveSnapshotAsync(EventSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIdentity(snapshot.SiteId, snapshot.StreamId);
        if (!string.Equals(_session.SiteId, snapshot.SiteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Snapshot site does not match the active command transaction."); }
        if (snapshot.Version <= 0 || snapshot.Version % 100 != 0)
        { throw new ArgumentOutOfRangeException(nameof(snapshot), "Snapshots are stored at each 100-event boundary."); }
        ValidateJson(snapshot.StateJson, nameof(snapshot));
        using var command = Command("""
            INSERT INTO es.Snapshots (SiteId, StreamId, Version, StateJson, RecordedAt)
            SELECT @site, @stream, @version, @state, @recorded
            WHERE EXISTS (
                SELECT 1 FROM es.Streams WITH (HOLDLOCK)
                WHERE SiteId = @site AND StreamId = @stream AND Version >= @version)
              AND NOT EXISTS (
                SELECT 1 FROM es.Snapshots WITH (UPDLOCK, HOLDLOCK)
                WHERE SiteId = @site AND StreamId = @stream AND Version >= @version);
            """);
        AddIdentity(command, snapshot.SiteId, snapshot.StreamId);
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = snapshot.Version;
        command.Parameters.Add("@state", SqlDbType.NVarChar, -1).Value = snapshot.StateJson;
        command.Parameters.Add("@recorded", SqlDbType.DateTimeOffset).Value = snapshot.RecordedAt;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException("Snapshot stream/version does not exist or snapshot already exists."); }
    }

    private async Task<bool> IsExactReplayAsync(string siteId, string streamId, long expectedVersion,
        ImmutableArray<NewStreamEvent> events, CancellationToken cancellationToken)
    {
        using var command = Command("""
            SELECT SourceEventId, EventType, SchemaVersion, PayloadJson, MetadataJson, OccurredAt, RecordedAt, CloudEventJson
            FROM es.Events WHERE SiteId = @site AND StreamId = @stream
                AND Version > @start AND Version <= @end ORDER BY Version;
            """);
        AddIdentity(command, siteId, streamId);
        command.Parameters.Add("@start", SqlDbType.BigInt).Value = expectedVersion;
        command.Parameters.Add("@end", SqlDbType.BigInt).Value = expectedVersion + events.Length;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < events.Length; i++)
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            { return false; }
            var item = events[i];
            if (await reader.GetFieldValueAsync<Guid>(0, cancellationToken).ConfigureAwait(false) != item.SourceEventId
                || !string.Equals(await reader.GetFieldValueAsync<string>(1, cancellationToken).ConfigureAwait(false), item.EventType, StringComparison.Ordinal)
                || await reader.GetFieldValueAsync<int>(2, cancellationToken).ConfigureAwait(false) != item.SchemaVersion
                || !string.Equals(await reader.GetFieldValueAsync<string>(3, cancellationToken).ConfigureAwait(false), item.PayloadJson, StringComparison.Ordinal)
                || !string.Equals(await reader.GetFieldValueAsync<string>(4, cancellationToken).ConfigureAwait(false), item.MetadataJson, StringComparison.Ordinal)
                || await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken).ConfigureAwait(false) != item.OccurredAt
                || await reader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken).ConfigureAwait(false) != item.RecordedAt
                || !string.Equals(await reader.GetFieldValueAsync<string>(7, cancellationToken).ConfigureAwait(false),
                    StoredCloudEventEnvelope.Serialize(siteId, item, _options.SourceApplicationName), StringComparison.Ordinal))
            { return false; }
        }
        return !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqlCommand Command(string sql)
    {
        var command = _session.CreateCommand(sql);
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        return command;
    }

    private async Task<SqlConnection> OpenReadAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddIdentity(SqlCommand command, string siteId, string streamId)
    {
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@stream", SqlDbType.VarChar, 200).Value = streamId;
    }

    private static void ValidateIdentity(string siteId, string streamId)
    {
        if (siteId is not { Length: 3 } || siteId.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c)))
        { throw new ArgumentException("Site ID must be three uppercase ASCII letters/digits.", nameof(siteId)); }
        if (string.IsNullOrWhiteSpace(streamId) || streamId.Length > 200)
        { throw new ArgumentException("Invalid stream ID.", nameof(streamId)); }
    }

    private static void ValidateEvents(ImmutableArray<NewStreamEvent> events)
    {
        var ids = new HashSet<Guid>();
        foreach (var item in events)
        {
            if (item.SourceEventId == Guid.Empty || !ids.Add(item.SourceEventId))
            { throw new ArgumentException("Source event identities must be unique and nonempty.", nameof(events)); }
            if (string.IsNullOrWhiteSpace(item.EventType) || item.EventType.Length > 200 || item.SchemaVersion < 1)
            { throw new ArgumentException("Invalid event type or schema version.", nameof(events)); }
            ValidateJson(item.PayloadJson, nameof(events));
            ValidateJson(item.MetadataJson, nameof(events));
        }
    }

    private static void ValidateJson(string json, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(json))
        { throw new ArgumentException("JSON cannot be empty.", parameterName); }
        try
        { using var _ = JsonDocument.Parse(json); }
        catch (JsonException exception) { throw new ArgumentException("Invalid JSON.", parameterName, exception); }
    }
}
