using System.Text.Json;
using System.Text.Json.Nodes;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Nvm.Bus.Topology;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;

namespace Nvm.Projections;

/// <summary>Inbox bền của genealogy: broker chỉ được ack sau khi fact nguồn đã nằm trong PostgreSQL.</summary>
public sealed class GenealogyProjectionInbox(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task EnqueueAsync(StoredStreamEvent fact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ProjectionIdentity.ValidateSite(fact.SiteId);
        if (!GenealogyProjection.Handles(fact.EventType))
        { throw new ArgumentException("Event is not a genealogy fact.", nameof(fact)); }
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO trace.inbox (site_id, source_event_id, stream_id, stream_version, global_sequence, fact)
            VALUES (@site, @event, @stream, @version, @sequence, @fact)
            ON CONFLICT (site_id, source_event_id) DO NOTHING;
            SELECT fact = @fact FROM trace.inbox WHERE site_id = @site AND source_event_id = @event;
            """, connection);
        insert.Parameters.AddWithValue("site", fact.SiteId);
        insert.Parameters.AddWithValue("event", fact.SourceEventId);
        insert.Parameters.AddWithValue("stream", fact.StreamId);
        insert.Parameters.AddWithValue("version", fact.Version);
        insert.Parameters.AddWithValue("sequence", fact.GlobalSequence);
        insert.Parameters.Add("fact", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(fact, Json);
        if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        { throw new InvalidDataException("Duplicate event ID carries a different source fact."); }
    }

    /// <summary>Áp dụng các fact đã sẵn sàng (version kế tiếp của stream) và ack chúng cùng transaction.</summary>
    public async Task<int> DispatchAsync(string siteId, int limit = 500, CancellationToken cancellationToken = default)
    {
        ProjectionIdentity.ValidateSite(siteId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await GenealogyProjection.LockAsync(connection, transaction, siteId, cancellationToken).ConfigureAwait(false);
        var facts = new List<StoredStreamEvent>();
        await using (var pending = new NpgsqlCommand("""
            SELECT i.fact::text FROM trace.inbox i
            LEFT JOIN trace.stream_progress p ON p.site_id = i.site_id AND p.stream_id = i.stream_id
            WHERE i.site_id = @site AND NOT i.applied
              AND i.stream_version <= coalesce(p.stream_version, 0) + 1
            ORDER BY i.global_sequence LIMIT @limit FOR UPDATE OF i;
            """, connection, transaction))
        {
            pending.Parameters.AddWithValue("site", siteId);
            pending.Parameters.AddWithValue("limit", limit);
            await using var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                facts.Add(JsonSerializer.Deserialize<StoredStreamEvent>(reader.GetString(0), Json)
                    ?? throw new InvalidDataException("Stored genealogy fact is null."));
            }
        }
        var applied = new List<Guid>();
        foreach (var fact in facts)
        {
            // Hai version của cùng stream trong một lô: bản sau chỉ áp dụng khi bản trước đã xong.
            await GenealogyProjection.ApplyAsync(connection, transaction, fact, cancellationToken).ConfigureAwait(false);
            applied.Add(fact.SourceEventId);
        }
        if (applied.Count > 0)
        {
            await using var acknowledge = new NpgsqlCommand("""
                UPDATE trace.inbox SET applied = true WHERE site_id = @site AND source_event_id = ANY(@events);
                """, connection, transaction);
            acknowledge.Parameters.AddWithValue("site", siteId);
            acknowledge.Parameters.AddWithValue("events", applied.ToArray());
            await acknowledge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return applied.Count;
    }
}

/// <summary>Nhận event genealogy từ bus, đối chiếu với fact đã commit ở SQL Server rồi đưa vào inbox.</summary>
[BusEndpoint("traceability", "genealogy-projection")]
public sealed class GenealogyProjectionConsumer(SqlGlobalEventFeed source, GenealogyProjectionInbox inbox)
    : IConsumer<MaterialLotConsumed>, IConsumer<UnitAssembledInto>, IConsumer<UnitRemovedFrom>,
        IConsumer<GenealogyCorrectionRecorded>, IConsumer<RollCoated>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task Consume(ConsumeContext<MaterialLotConsumed> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<UnitAssembledInto> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<UnitRemovedFrom> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<GenealogyCorrectionRecorded> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<RollCoated> context) => CaptureAsync(context);

    private async Task CaptureAsync<T>(ConsumeContext<T> context) where T : class, IDomainEvent
    {
        var message = context.Message;
        var fact = await source.FindAsync(message.SiteId, message.EventId, context.CancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Genealogy event is absent from its committed source.");
        if (fact.EventType != EventTypeName.Of(typeof(T)).Value || fact.SchemaVersion != 1)
        { throw new InvalidDataException("Broker event contract differs from the committed source."); }
        // So bằng JSON thay vì record equality: ImmutableArray so sánh theo tham chiếu.
        if (!JsonNode.DeepEquals(JsonNode.Parse(fact.PayloadJson),
                JsonNode.Parse(JsonSerializer.Serialize(message, Json))))
        { throw new InvalidDataException("Broker event differs from the committed source payload."); }
        await inbox.EnqueueAsync(fact, context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Chỉ xử lý inbox bền; không quét lại lịch sử SQL mỗi vòng.</summary>
public sealed class GenealogyProjectionWorker(GenealogyProjectionInbox inbox, IEnumerable<string> sites,
    TimeProvider clock, ILogger<GenealogyProjectionWorker> logger) : BackgroundService
{
    private readonly string[] _sites = sites.Distinct(StringComparer.Ordinal).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var site in _sites)
        { ProjectionIdentity.ValidateSite(site); }
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            var failed = false;
            foreach (var site in _sites)
            {
                try
                { processed += await inbox.DispatchAsync(site, cancellationToken: stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                { return; }
                catch (Exception error)
                {
                    failed = true;
                    logger.LogError(error, "Genealogy projection failed for {SiteId}; inbox retained for retry", site);
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
