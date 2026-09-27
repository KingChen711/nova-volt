using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.ProductionExecution.Handlers;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Work order trên SQL, trong transaction của command.</summary>
public sealed class SqlWorkOrderStore(SqlCommandSession session) : IWorkOrderStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StoredWorkOrder?> LoadForUpdateAsync(string siteId, string workOrderId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT WorkOrderId, ScheduleId, ExternalProductCode, ProductCode, EarliestStart, MaterialsJson, Status,
                OpenTaskIdsJson, StreamVersion
            FROM execution.WorkOrders WITH (UPDLOCK, HOLDLOCK) WHERE SiteId = @site AND WorkOrderId = @id;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.NVarChar, 50).Value = workOrderId;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? await ReadAsync(reader, cancellationToken)
            .ConfigureAwait(false) : null;
    }

    public async Task AddAsync(string siteId, StoredWorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO execution.WorkOrders (SiteId, WorkOrderId, ScheduleId, ExternalProductCode, ProductCode, EarliestStart,
                MaterialsJson, Status, OpenTaskIdsJson, StreamVersion, ReceivedAt, UpdatedAt)
            VALUES (@site, @id, @schedule, @external, @product, @start, @materials, @status, @tasks, @version, @at, @at);
            """);
        Bind(command, siteId, workOrder, at);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(string siteId, StoredWorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE execution.WorkOrders SET ProductCode = @product, MaterialsJson = @materials, Status = @status,
                OpenTaskIdsJson = @tasks, StreamVersion = @version, UpdatedAt = @at
            WHERE SiteId = @site AND WorkOrderId = @id AND ScheduleId = @schedule AND ExternalProductCode = @external
              AND (EarliestStart = @start OR (EarliestStart IS NULL AND @start IS NULL));
            """);
        Bind(command, siteId, workOrder, at);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException($"Work order {workOrder.WorkOrderId} changed its ERP identity inside a transaction."); }
    }

    internal static async Task<StoredWorkOrder> ReadAsync(SqlDataReader reader, CancellationToken cancellationToken) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken).ConfigureAwait(false),
            JsonSerializer.Deserialize<ImmutableArray<WorkOrderMaterial>>(reader.GetString(5), Json),
            reader.GetString(6), JsonSerializer.Deserialize<ImmutableArray<string>>(reader.GetString(7), Json), reader.GetInt64(8));

    private static void Bind(SqlCommand command, string siteId, StoredWorkOrder workOrder, DateTimeOffset at)
    {
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.NVarChar, 50).Value = workOrder.WorkOrderId;
        command.Parameters.Add("@schedule", SqlDbType.NVarChar, 50).Value = workOrder.ScheduleId;
        command.Parameters.Add("@external", SqlDbType.NVarChar, 100).Value = workOrder.ExternalProductCode;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = (object?)workOrder.ProductCode ?? DBNull.Value;
        command.Parameters.Add("@start", SqlDbType.DateTimeOffset).Value = (object?)workOrder.EarliestStart ?? DBNull.Value;
        command.Parameters.Add("@materials", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(workOrder.Materials, Json);
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = workOrder.Status;
        command.Parameters.Add("@tasks", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(workOrder.OpenTaskIds, Json);
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = workOrder.StreamVersion;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
    }

    private void Require(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Work order site does not match the active command transaction."); }
    }
}

/// <summary>Đọc work order của đúng một site (không qua transaction command).</summary>
public sealed class WorkOrderQueries(SqlCommandStoreOptions options)
{
    public async Task<IReadOnlyList<StoredWorkOrder>> ListAsync(string siteId, string? status, int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (@limit) WorkOrderId, ScheduleId, ExternalProductCode, ProductCode, EarliestStart, MaterialsJson, Status,
                OpenTaskIdsJson, StreamVersion
            FROM execution.WorkOrders WHERE SiteId = @site AND (@status IS NULL OR Status = @status)
            ORDER BY UpdatedAt, WorkOrderId;
            """, connection);
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = (object?)status ?? DBNull.Value;
        var rows = new List<StoredWorkOrder>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { rows.Add(await SqlWorkOrderStore.ReadAsync(reader, cancellationToken).ConfigureAwait(false)); }
        return rows;
    }
}
