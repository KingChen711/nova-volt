using System.Text.Json;
using System.Text.Json.Nodes;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Kernel.EventSourcing;

namespace Nvm.Projections;

/// <summary>
/// Một read model PostgreSQL được dựng từ fact đã commit. Runner bảo đảm mỗi stream nguồn được áp dụng
/// đúng thứ tự version, đúng một lần, trong cùng transaction với tiến độ của nó.
/// </summary>
public interface IOrderedProjection
{
    /// <summary>Tên ổn định; đổi tên nghĩa là một projection mới dựng lại từ đầu.</summary>
    string Name { get; }

    bool Accepts(string eventType);

    /// <summary>Áp dụng một fact; runner đã kiểm version và sẽ ghi tiến độ sau khi hàm này trả về.</summary>
    Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredStreamEvent fact,
        CancellationToken cancellationToken);

    /// <summary>Xoá read model của một site trước khi rebuild (chạy bằng credential chủ schema).</summary>
    Task ResetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteId,
        CancellationToken cancellationToken);
}

/// <summary>Inbox + dispatch + catch-up + rebuild dùng chung cho các <see cref="IOrderedProjection"/>.</summary>
public sealed class OrderedProjectionRunner(NpgsqlDataSource dataSource, IGlobalEventFeed feed,
    IEnumerable<IOrderedProjection> projections)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IOrderedProjection[] _projections = [.. projections];

    public IReadOnlyList<IOrderedProjection> Projections => _projections;

    /// <summary>Ghi fact vào inbox của mọi projection quan tâm; broker ack sau khi hàm này commit.</summary>
    public async Task<int> EnqueueAsync(StoredStreamEvent fact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ProjectionIdentity.ValidateSite(fact.SiteId);
        var targets = _projections.Where(p => p.Accepts(fact.EventType)).ToArray();
        if (targets.Length == 0)
        { return 0; }
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(fact, Json);
        foreach (var projection in targets)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO rm.projection_inbox (projection_name, site_id, source_event_id, stream_id, stream_version,
                    global_sequence, fact)
                VALUES (@name, @site, @event, @stream, @version, @sequence, @fact)
                ON CONFLICT (projection_name, site_id, source_event_id) DO NOTHING;
                SELECT fact = @fact FROM rm.projection_inbox
                WHERE projection_name = @name AND site_id = @site AND source_event_id = @event;
                """, connection, transaction);
            insert.Parameters.AddWithValue("name", projection.Name);
            insert.Parameters.AddWithValue("site", fact.SiteId);
            insert.Parameters.AddWithValue("event", fact.SourceEventId);
            insert.Parameters.AddWithValue("stream", fact.StreamId);
            insert.Parameters.AddWithValue("version", fact.Version);
            insert.Parameters.AddWithValue("sequence", fact.GlobalSequence);
            insert.Parameters.Add("fact", NpgsqlDbType.Jsonb).Value = json;
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            { throw new InvalidDataException("Duplicate event ID carries a different source fact."); }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return targets.Length;
    }

    /// <summary>Áp dụng fact sẵn sàng của mọi projection trong một site; trả số fact đã áp dụng.</summary>
    public async Task<int> DispatchAsync(string siteId, int limit = 500, CancellationToken cancellationToken = default)
    {
        ProjectionIdentity.ValidateSite(siteId);
        var total = 0;
        foreach (var projection in _projections)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockAsync(connection, transaction, projection.Name, siteId, cancellationToken).ConfigureAwait(false);
            var facts = new List<StoredStreamEvent>();
            await using (var pending = new NpgsqlCommand("""
                SELECT i.fact::text FROM rm.projection_inbox i
                LEFT JOIN rm.projection_stream_progress p ON p.projection_name = i.projection_name
                    AND p.site_id = i.site_id AND p.stream_id = i.stream_id
                WHERE i.projection_name = @name AND i.site_id = @site AND NOT i.applied
                  AND i.stream_version <= coalesce(p.stream_version, 0) + 1
                ORDER BY i.global_sequence LIMIT @limit FOR UPDATE OF i;
                """, connection, transaction))
            {
                pending.Parameters.AddWithValue("name", projection.Name);
                pending.Parameters.AddWithValue("site", siteId);
                pending.Parameters.AddWithValue("limit", limit);
                await using var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    facts.Add(JsonSerializer.Deserialize<StoredStreamEvent>(reader.GetString(0), Json)
                        ?? throw new InvalidDataException("Stored projection fact is null."));
                }
            }
            var applied = new List<Guid>();
            foreach (var fact in facts)
            {
                if (await ApplyOrderedAsync(connection, transaction, projection, fact, cancellationToken).ConfigureAwait(false))
                { applied.Add(fact.SourceEventId); }
            }
            if (applied.Count > 0)
            {
                await using var acknowledge = new NpgsqlCommand("""
                    UPDATE rm.projection_inbox SET applied = true
                    WHERE projection_name = @name AND site_id = @site AND source_event_id = ANY(@events);
                    """, connection, transaction);
                acknowledge.Parameters.AddWithValue("name", projection.Name);
                acknowledge.Parameters.AddWithValue("site", siteId);
                acknowledge.Parameters.AddWithValue("events", applied.ToArray());
                await acknowledge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            total += applied.Count;
        }
        return total;
    }

    /// <summary>Đối chiếu: quét feed của site, áp dụng fact còn thiếu cho mọi projection.</summary>
    public async Task CatchUpAsync(string siteId, string? onlyProjection = null, int batchSize = 1000,
        CancellationToken cancellationToken = default)
    {
        ProjectionIdentity.ValidateSite(siteId);
        var targets = _projections.Where(p => onlyProjection is null || p.Name == onlyProjection).ToArray();
        long cursor = 0;
        while (true)
        {
            var batch = await feed.ReadAsync(siteId, cursor, batchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            { break; }
            foreach (var projection in targets)
            {
                var handled = batch.Where(fact => projection.Accepts(fact.EventType)).ToList();
                if (handled.Count == 0)
                { continue; }
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await LockAsync(connection, transaction, projection.Name, siteId, cancellationToken).ConfigureAwait(false);
                foreach (var fact in handled)
                { await ApplyOrderedAsync(connection, transaction, projection, fact, cancellationToken).ConfigureAwait(false); }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            cursor = batch[^1].GlobalSequence;
            if (batch.Count < batchSize)
            { break; }
        }
    }

    /// <summary>Xoá rồi dựng lại một projection của site từ feed (credential chủ schema).</summary>
    public async Task RebuildAsync(string siteId, string projectionName, CancellationToken cancellationToken = default)
    {
        var projection = _projections.Single(p => p.Name == projectionName);
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await LockAsync(connection, transaction, projection.Name, siteId, cancellationToken).ConfigureAwait(false);
            await projection.ResetAsync(connection, transaction, siteId, cancellationToken).ConfigureAwait(false);
            await using var reset = new NpgsqlCommand("""
                DELETE FROM rm.projection_stream_progress WHERE projection_name = @name AND site_id = @site;
                UPDATE rm.projection_inbox SET applied = false WHERE projection_name = @name AND site_id = @site;
                """, connection, transaction);
            reset.Parameters.AddWithValue("name", projection.Name);
            reset.Parameters.AddWithValue("site", siteId);
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await CatchUpAsync(siteId, projectionName, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var connection2 = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var acknowledge = new NpgsqlCommand("""
            UPDATE rm.projection_inbox i SET applied = true FROM rm.projection_stream_progress p
            WHERE i.projection_name = @name AND i.site_id = @site AND p.projection_name = i.projection_name
              AND p.site_id = i.site_id AND p.stream_id = i.stream_id AND i.stream_version <= p.stream_version;
            """, connection2);
        acknowledge.Parameters.AddWithValue("name", projection.Name);
        acknowledge.Parameters.AddWithValue("site", siteId);
        await acknowledge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ApplyOrderedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IOrderedProjection projection, StoredStreamEvent fact, CancellationToken cancellationToken)
    {
        long applied;
        await using (var progress = new NpgsqlCommand("""
            SELECT stream_version FROM rm.projection_stream_progress
            WHERE projection_name = @name AND site_id = @site AND stream_id = @stream FOR UPDATE;
            """, connection, transaction))
        {
            progress.Parameters.AddWithValue("name", projection.Name);
            progress.Parameters.AddWithValue("site", fact.SiteId);
            progress.Parameters.AddWithValue("stream", fact.StreamId);
            applied = (long?)await progress.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        }
        if (applied >= fact.Version)
        { return true; }
        if (applied != fact.Version - 1)
        { throw new InvalidDataException($"{projection.Name}: stream version has a gap."); }
        await projection.ApplyAsync(connection, transaction, fact, cancellationToken).ConfigureAwait(false);
        await using var advance = new NpgsqlCommand("""
            INSERT INTO rm.projection_stream_progress (projection_name, site_id, stream_id, stream_version)
            VALUES (@name, @site, @stream, @version)
            ON CONFLICT (projection_name, site_id, stream_id) DO UPDATE SET stream_version = excluded.stream_version;
            """, connection, transaction);
        advance.Parameters.AddWithValue("name", projection.Name);
        advance.Parameters.AddWithValue("site", fact.SiteId);
        advance.Parameters.AddWithValue("stream", fact.StreamId);
        advance.Parameters.AddWithValue("version", fact.Version);
        await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name,
        string siteId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO rm.projection_checkpoint(site_id, projection_name) VALUES (@site, @name) ON CONFLICT DO NOTHING;
            SELECT generation FROM rm.projection_checkpoint WHERE site_id = @site AND projection_name = @name FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Đối chiếu event từ bus với fact đã commit ở SQL Server rồi đưa vào inbox dùng chung.</summary>
public abstract class OrderedProjectionConsumer(SqlGlobalEventFeed source, OrderedProjectionRunner runner)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected async Task CaptureAsync<T>(ConsumeContext<T> context) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;
        var fact = await source.FindAsync(message.SiteId, message.EventId, context.CancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Projection event is absent from its committed source.");
        if (fact.EventType != EventTypeName.Of(typeof(T)).Value)
        { throw new InvalidDataException("Broker event contract differs from the committed source."); }
        if (!JsonNode.DeepEquals(JsonNode.Parse(fact.PayloadJson), JsonNode.Parse(JsonSerializer.Serialize(message, Json))))
        { throw new InvalidDataException("Broker event differs from the committed source payload."); }
        await runner.EnqueueAsync(fact, context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Vòng dispatch inbox dùng chung; lỗi một site không chặn site khác.</summary>
public sealed class OrderedProjectionWorker(OrderedProjectionRunner runner, IEnumerable<string> sites,
    TimeProvider clock, ILogger<OrderedProjectionWorker> logger) : BackgroundService
{
    private readonly string[] _sites = sites.Distinct(StringComparer.Ordinal).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            var failed = false;
            foreach (var site in _sites)
            {
                try
                { processed += await runner.DispatchAsync(site, cancellationToken: stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                { return; }
                catch (Exception error)
                {
                    failed = true;
                    logger.LogError(error, "Ordered projections failed for {SiteId}; inbox retained for retry", site);
                }
            }
            if (processed == 0 || failed)
            {
                await Task.Delay(failed ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(250), clock, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
