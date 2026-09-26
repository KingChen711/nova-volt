using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.Kernel.EventSourcing;

namespace Nvm.EventStore;

/// <summary>Một fact kèm stream đích, dùng cho nạp hàng loạt.</summary>
public sealed record BulkStreamEvent(string StreamId, string StreamType, NewStreamEvent Event);

/// <summary>
/// Nạp hàng loạt fact vào event store cho seed/lab (credential chủ schema, stream phải chưa tồn tại).
/// Outbox được ghi kèm với <c>DispatchedAt</c> = thời điểm ghi: dữ liệu seed không bị phát lên bus.
/// </summary>
public static class SqlEventBulkLoader
{
    public static async Task<long> LoadAsync(string connectionString, string siteId,
        IEnumerable<BulkStreamEvent> events, string sourceApplicationName = "seed",
        int batchSize = 50_000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(events);
        var versions = new Dictionary<string, (string Type, long Version, DateTimeOffset CreatedAt)>(StringComparer.Ordinal);
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var eventRows = NewEventTable();
        var outboxRows = NewOutboxTable();
        long loaded = 0;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in events)
        {
            var current = versions.GetValueOrDefault(item.StreamId, (item.StreamType, 0, item.Event.RecordedAt));
            if (current.Type != item.StreamType)
            { throw new InvalidOperationException($"Stream {item.StreamId} changes type."); }
            var version = current.Version + 1;
            versions[item.StreamId] = (item.StreamType, version, current.CreatedAt);
            dirty.Add(item.StreamId);
            var envelope = StoredCloudEventEnvelope.Serialize(siteId, item.Event, sourceApplicationName);
            eventRows.Rows.Add(siteId, item.StreamId, version, item.Event.SourceEventId, item.Event.EventType,
                item.Event.SchemaVersion, item.Event.PayloadJson, item.Event.MetadataJson, envelope,
                item.Event.OccurredAt, item.Event.RecordedAt);
            outboxRows.Rows.Add(item.Event.SourceEventId, siteId, item.StreamId, version, item.Event.EventType,
                item.Event.SchemaVersion, item.Event.PayloadJson, item.Event.MetadataJson, item.Event.OccurredAt,
                item.Event.RecordedAt, item.Event.RecordedAt, item.Event.RecordedAt, 0, item.Event.RecordedAt);
            loaded++;
            if (eventRows.Rows.Count >= batchSize)
            { await FlushAsync(connection, versions, dirty, eventRows, outboxRows, siteId, cancellationToken).ConfigureAwait(false); }
        }
        await FlushAsync(connection, versions, dirty, eventRows, outboxRows, siteId, cancellationToken).ConfigureAwait(false);
        return loaded;
    }

    private static async Task FlushAsync(SqlConnection connection,
        Dictionary<string, (string Type, long Version, DateTimeOffset CreatedAt)> versions, HashSet<string> dirty,
        DataTable events, DataTable outbox, string siteId, CancellationToken cancellationToken)
    {
        if (events.Rows.Count == 0)
        { return; }
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var streams = NewStreamTable();
        foreach (var stream in dirty)
        {
            var head = versions[stream];
            streams.Rows.Add(siteId, stream, head.Type, head.Version, head.CreatedAt);
        }
        // Header stream được MERGE lại mỗi lô: lô sau có thể nối tiếp stream của lô trước.
        await using (var stage = new SqlCommand("""
            CREATE TABLE #streams (SiteId varchar(3) COLLATE Latin1_General_100_BIN2, StreamId varchar(200)
                COLLATE Latin1_General_100_BIN2, StreamType varchar(100) COLLATE Latin1_General_100_BIN2,
                Version bigint, CreatedAt datetimeoffset(7));
            """, connection, transaction))
        { await stage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await CopyAsync(connection, transaction, "#streams", streams, cancellationToken).ConfigureAwait(false);
        await using (var merge = new SqlCommand("""
            MERGE es.Streams AS target USING #streams AS source
            ON target.SiteId = source.SiteId AND target.StreamId = source.StreamId
            WHEN MATCHED THEN UPDATE SET Version = source.Version
            WHEN NOT MATCHED THEN INSERT (SiteId, StreamId, StreamType, Version, CreatedAt)
                VALUES (source.SiteId, source.StreamId, source.StreamType, source.Version, source.CreatedAt);
            DROP TABLE #streams;
            """, connection, transaction) { CommandTimeout = 600 })
        { await merge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await CopyAsync(connection, transaction, "es.Events", events, cancellationToken).ConfigureAwait(false);
        await CopyAsync(connection, transaction, "es.Outbox", outbox, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        events.Rows.Clear();
        outbox.Rows.Clear();
        dirty.Clear();
    }

    private static async Task CopyAsync(SqlConnection connection, SqlTransaction transaction, string table,
        DataTable rows, CancellationToken cancellationToken)
    {
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        { DestinationTableName = table, BulkCopyTimeout = 600, BatchSize = 10_000 };
        foreach (DataColumn column in rows.Columns)
        { copy.ColumnMappings.Add(column.ColumnName, column.ColumnName); }
        await copy.WriteToServerAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    private static DataTable NewStreamTable()
    {
        var table = new DataTable();
        table.Columns.Add("SiteId", typeof(string));
        table.Columns.Add("StreamId", typeof(string));
        table.Columns.Add("StreamType", typeof(string));
        table.Columns.Add("Version", typeof(long));
        table.Columns.Add("CreatedAt", typeof(DateTimeOffset));
        return table;
    }

    private static DataTable NewEventTable()
    {
        var table = new DataTable();
        table.Columns.Add("SiteId", typeof(string));
        table.Columns.Add("StreamId", typeof(string));
        table.Columns.Add("Version", typeof(long));
        table.Columns.Add("SourceEventId", typeof(Guid));
        table.Columns.Add("EventType", typeof(string));
        table.Columns.Add("SchemaVersion", typeof(int));
        table.Columns.Add("PayloadJson", typeof(string));
        table.Columns.Add("MetadataJson", typeof(string));
        table.Columns.Add("CloudEventJson", typeof(string));
        table.Columns.Add("OccurredAt", typeof(DateTimeOffset));
        table.Columns.Add("RecordedAt", typeof(DateTimeOffset));
        return table;
    }

    private static DataTable NewOutboxTable()
    {
        var table = new DataTable();
        table.Columns.Add("EventId", typeof(Guid));
        table.Columns.Add("SiteId", typeof(string));
        table.Columns.Add("StreamId", typeof(string));
        table.Columns.Add("Version", typeof(long));
        table.Columns.Add("EventType", typeof(string));
        table.Columns.Add("SchemaVersion", typeof(int));
        table.Columns.Add("PayloadJson", typeof(string));
        table.Columns.Add("MetadataJson", typeof(string));
        table.Columns.Add("OccurredAt", typeof(DateTimeOffset));
        table.Columns.Add("RecordedAt", typeof(DateTimeOffset));
        table.Columns.Add("CreatedAt", typeof(DateTimeOffset));
        table.Columns.Add("DispatchedAt", typeof(DateTimeOffset));
        table.Columns.Add("Attempt", typeof(int));
        table.Columns.Add("NextAttemptAt", typeof(DateTimeOffset));
        return table;
    }
}
