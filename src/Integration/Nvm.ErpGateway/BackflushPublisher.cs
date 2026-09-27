using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.Material;
using Nvm.Kernel.Identity;

namespace Nvm.ErpGateway;

/// <summary>Một dòng backflush: tổng tiêu hao của một lot trong lô.</summary>
public sealed record BackflushLine(string MaterialCode, string LotId, string LotKind, string UnitOfMeasure, decimal Quantity,
    int Consumptions);

/// <summary>Lô gửi ERP. <c>BatchId</c> tất định theo khoảng sequence: ERP dùng nó để bỏ lô trùng.</summary>
public sealed record BackflushBatch(Guid BatchId, string SiteId, long FromSequence, long ToSequence,
    IReadOnlyList<BackflushLine> Lines);

public sealed record BackflushRunResult(bool Sent, Guid? BatchId, int Events, int? ErpStatus);

/// <summary>
/// Đẩy tiêu hao vật liệu (event <c>MaterialLotConsumed</c> đã commit) lên ERP theo lô. Khoảng sequence của lô được chốt
/// trước khi gửi và chỉ tiến checkpoint khi ERP trả 2xx: không mất và không chồng lô.
/// </summary>
public sealed class BackflushPublisher(SqlCommandStoreOptions store, ErpGatewayOptions options, HttpClient http,
    TimeProvider clock, ILogger<BackflushPublisher> logger)
{
    public const int MaxEventsPerBatch = 10_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string ConsumedType = EventTypeName.Of(typeof(MaterialLotConsumed)).Value;
    private static readonly Guid BatchNamespace = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace,
        "backflush.novavolt.example");

    public async Task<BackflushRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (options.ErpBaseAddress is null)
        { return new BackflushRunResult(false, null, 0, null); }
        await using var connection = new SqlConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (last, pendingFrom, pendingTo, pendingBatch) = await CheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
        List<(long Sequence, MaterialLotConsumed Fact)> facts;
        Guid batchId;
        if (pendingBatch is { } pending)
        {
            facts = await ReadAsync(connection, pendingFrom!.Value, pendingTo!.Value, cancellationToken).ConfigureAwait(false);
            batchId = pending;
        }
        else
        {
            facts = await ReadAsync(connection, last + 1, long.MaxValue, cancellationToken).ConfigureAwait(false);
            if (facts.Count == 0)
            { return new BackflushRunResult(false, null, 0, null); }
            pendingFrom = facts[0].Sequence;
            pendingTo = facts[^1].Sequence;
            batchId = DeterministicGuid.CreateVersion5(BatchNamespace, $"{options.SiteId}:{pendingFrom}:{pendingTo}");
            await SetPendingAsync(connection, pendingFrom.Value, pendingTo.Value, batchId, cancellationToken).ConfigureAwait(false);
        }
        var batch = new BackflushBatch(batchId, options.SiteId, pendingFrom!.Value, pendingTo!.Value,
            [.. facts.GroupBy(f => (f.Fact.MaterialCode, f.Fact.LotId, f.Fact.LotKind, f.Fact.UnitOfMeasure))
                .OrderBy(g => g.Key.MaterialCode, StringComparer.Ordinal).ThenBy(g => g.Key.LotId, StringComparer.Ordinal)
                .Select(g => new BackflushLine(g.Key.MaterialCode, g.Key.LotId, g.Key.LotKind, g.Key.UnitOfMeasure,
                    g.Sum(f => f.Fact.Quantity), g.Count()))]);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.ErpBaseAddress, "api/backflush"))
        { Content = JsonContent.Create(batch, options: Json) };
        request.Headers.Add("Idempotency-Key", batchId.ToString("D"));
        HttpResponseMessage response;
        try
        { response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException error)
        {
            logger.LogWarning(error, "Backflush batch {BatchId} not delivered; will resend the same range", batchId);
            return new BackflushRunResult(false, batchId, facts.Count, null);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Backflush batch {BatchId} refused with {Status}; will resend", batchId, (int)response.StatusCode);
                return new BackflushRunResult(false, batchId, facts.Count, (int)response.StatusCode);
            }
            await CompleteAsync(connection, pendingTo.Value, cancellationToken).ConfigureAwait(false);
            return new BackflushRunResult(true, batchId, facts.Count, (int)response.StatusCode);
        }
    }

    private async Task<(long Last, long? From, long? To, Guid? Batch)> CheckpointAsync(SqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM erp.BackflushCheckpoints WHERE SiteId = @site)
                INSERT INTO erp.BackflushCheckpoints (SiteId, LastGlobalSequence, UpdatedAt) VALUES (@site, 0, @at);
            SELECT LastGlobalSequence, PendingFrom, PendingTo, PendingBatchId FROM erp.BackflushCheckpoints WHERE SiteId = @site;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = options.SiteId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = clock.GetUtcNow();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        long? Optional(int index, bool isNull) => isNull ? null : reader.GetInt64(index);
        var fromIsNull = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false);
        var toIsNull = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false);
        var batchIsNull = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), Optional(1, fromIsNull), Optional(2, toIsNull), batchIsNull ? null : reader.GetGuid(3));
    }

    private async Task<List<(long, MaterialLotConsumed)>> ReadAsync(SqlConnection connection, long from, long to,
        CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("""
            SELECT TOP (@limit) GlobalSequence, PayloadJson FROM es.Events
            WHERE SiteId = @site AND EventType = @type AND GlobalSequence BETWEEN @from AND @to
            ORDER BY GlobalSequence;
            """, connection);
        command.Parameters.Add("@limit", SqlDbType.Int).Value = MaxEventsPerBatch;
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = options.SiteId;
        command.Parameters.Add("@type", SqlDbType.VarChar, 200).Value = ConsumedType;
        command.Parameters.Add("@from", SqlDbType.BigInt).Value = from;
        command.Parameters.Add("@to", SqlDbType.BigInt).Value = to;
        var rows = new List<(long, MaterialLotConsumed)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetInt64(0), JsonSerializer.Deserialize<MaterialLotConsumed>(reader.GetString(1), Json)
                ?? throw new InvalidDataException("Empty MaterialLotConsumed payload.")));
        }
        return rows;
    }

    private async Task SetPendingAsync(SqlConnection connection, long from, long to, Guid batchId, CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("""
            UPDATE erp.BackflushCheckpoints SET PendingFrom = @from, PendingTo = @to, PendingBatchId = @batch, UpdatedAt = @at
            WHERE SiteId = @site AND PendingBatchId IS NULL;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = options.SiteId;
        command.Parameters.Add("@from", SqlDbType.BigInt).Value = from;
        command.Parameters.Add("@to", SqlDbType.BigInt).Value = to;
        command.Parameters.Add("@batch", SqlDbType.UniqueIdentifier).Value = batchId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = clock.GetUtcNow();
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException("Another backflush publisher holds a pending batch for this site."); }
    }

    private async Task CompleteAsync(SqlConnection connection, long to, CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("""
            UPDATE erp.BackflushCheckpoints SET LastGlobalSequence = @to, PendingFrom = NULL, PendingTo = NULL,
                PendingBatchId = NULL, UpdatedAt = @at
            WHERE SiteId = @site AND PendingTo = @to;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = options.SiteId;
        command.Parameters.Add("@to", SqlDbType.BigInt).Value = to;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = clock.GetUtcNow();
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
