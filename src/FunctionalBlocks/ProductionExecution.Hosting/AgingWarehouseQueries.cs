using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Một cell đang nằm trong kho aging.</summary>
public sealed record AgingCell(string SerialNumber, string TrayId, string RackId, int Level, int Channel,
    decimal Ocv1Millivolt, DateTimeOffset AgingDueAt, string State);

/// <summary>
/// Truy vấn vận hành kho aging trên bảng trạng thái của chính FB (index lọc theo rack/level), site luôn lấy
/// từ token. Không đi qua read model vì trạng thái chờ đo là thứ người vận hành cần thấy ngay.
/// </summary>
public sealed class AgingWarehouseQueries(SqlCommandStoreOptions options, TimeProvider clock)
{
    public Task<IReadOnlyList<AgingCell>> AtRackLevelAsync(string siteId, string rackId, int level,
        CancellationToken cancellationToken) => QueryAsync("""
            SELECT TOP (2000) SerialNumber, TrayId, RackId, Level, AgingChannel, Ocv1Millivolt, AgingDueAt, State
            FROM execution.FormationAging
            WHERE SiteId = @site AND RackId = @rack AND Level = @level AND State = 'Aging'
            ORDER BY AgingChannel;
            """, command =>
        {
            command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
            command.Parameters.Add("@rack", SqlDbType.NVarChar, 20).Value = rackId;
            command.Parameters.Add("@level", SqlDbType.Int).Value = level;
        }, cancellationToken);

    /// <summary>Cell đang aging sắp tới hạn, hoặc đã tới hạn và đang chờ đo OCV lần 2.</summary>
    public Task<IReadOnlyList<AgingCell>> DueSoonAsync(string siteId, TimeSpan within, CancellationToken cancellationToken) =>
        QueryAsync("""
            SELECT TOP (2000) SerialNumber, TrayId, RackId, Level, AgingChannel, Ocv1Millivolt, AgingDueAt, State
            FROM execution.FormationAging
            WHERE SiteId = @site AND State IN ('Aging', 'AwaitingMeasurement') AND AgingDueAt <= @until
            ORDER BY AgingDueAt;
            """, command =>
        {
            command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
            command.Parameters.Add("@until", SqlDbType.DateTimeOffset).Value = clock.GetUtcNow() + within;
        }, cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "SQL là hằng trong class này; giá trị người dùng đi qua parameter.")]
    private async Task<IReadOnlyList<AgingCell>> QueryAsync(string sql, Action<SqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        bind(command);
        var cells = new List<AgingCell>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cells.Add(new AgingCell(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetDecimal(5),
                await reader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken).ConfigureAwait(false),
                reader.GetString(7)));
        }
        return cells;
    }

    public static IEndpointRouteBuilder MapAgingWarehouse(IEndpointRouteBuilder endpoints, string policy)
    {
        var group = endpoints.MapGroup("/api/v1/aging").RequireAuthorization(policy);
        group.MapGet("/racks/{rackId}/levels/{level:int}", async (string rackId, int level, HttpContext context,
            AgingWarehouseQueries queries, CancellationToken ct) => rackId.Length is 0 or > 20 || level is < 1 or > 50
                ? Results.BadRequest()
                : Results.Ok(await queries.AtRackLevelAsync(context.User.FindFirst("site_id")!.Value, rackId, level, ct)));
        group.MapGet("/due", async (int? withinHours, HttpContext context, AgingWarehouseQueries queries,
            CancellationToken ct) => withinHours is < 0 or > 24 * 30
                ? Results.BadRequest()
                : Results.Ok(await queries.DueSoonAsync(context.User.FindFirst("site_id")!.Value,
                    TimeSpan.FromHours(withinHours ?? 24), ct)));
        return endpoints;
    }
}
