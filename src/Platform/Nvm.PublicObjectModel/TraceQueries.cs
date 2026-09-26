using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Nvm.PublicObjectModel;

/// <summary>Một node trong kết quả trace: loại (lot, roll, cell, module, pack) và định danh.</summary>
public sealed record TraceNode(string Type, string Id);

/// <summary>Kết quả trace của một gốc trong site của người gọi.</summary>
public sealed record TraceResult(string SiteId, TraceNode Root, IReadOnlyList<TraceNode> Nodes);

/// <summary>
/// Truy vấn genealogy trên read model PostgreSQL. Forward/backward mặc định đọc closure table
/// (<c>rm.genealogy_closure</c>); biến thể recursive CTE giữ lại để so sánh ở ADR-006.
/// Site luôn lấy từ token, không từ tham số.
/// </summary>
public sealed class TraceQueries(NpgsqlDataSource dataSource)
{
    private static readonly Dictionary<string, short> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lot"] = 1,
        ["cell"] = 2,
        ["module"] = 3,
        ["pack"] = 4,
        ["roll"] = 5,
    };

    private static string TypeName(short type) => type switch
    {
        1 => "lot",
        2 => "cell",
        3 => "module",
        4 => "pack",
        5 => "roll",
        6 => "tray",
        _ => throw new InvalidDataException($"Unknown node type {type}.")
    };

    public static bool TryType(string? name, out short type)
    {
        type = 0;
        return name is not null && Types.TryGetValue(name, out type);
    }

    /// <summary>Mọi hậu duệ (có thể lọc theo loại) của một node, qua closure table.</summary>
    public Task<IReadOnlyList<TraceNode>> ForwardAsync(string siteId, short rootType, string rootId, short? descendantType,
        CancellationToken cancellationToken) => QueryAsync("""
            SELECT descendant_type, descendant_id FROM rm.genealogy_closure
            WHERE site_id = @site AND ancestor_type = @type AND ancestor_id = @id
              AND (@filter::smallint IS NULL OR descendant_type = @filter::smallint)
            ORDER BY descendant_type, descendant_id;
            """, siteId, rootType, rootId, descendantType, cancellationToken);

    /// <summary>Mọi tổ tiên (có thể lọc theo loại) của một node, qua closure table.</summary>
    public Task<IReadOnlyList<TraceNode>> BackwardAsync(string siteId, short rootType, string rootId, short? ancestorType,
        CancellationToken cancellationToken) => QueryAsync("""
            SELECT ancestor_type, ancestor_id FROM rm.genealogy_closure
            WHERE site_id = @site AND descendant_type = @type AND descendant_id = @id
              AND (@filter::smallint IS NULL OR ancestor_type = @filter::smallint)
            ORDER BY ancestor_type, ancestor_id;
            """, siteId, rootType, rootId, ancestorType, cancellationToken);

    /// <summary>Forward bằng recursive CTE trên bảng cạnh, không dùng closure (để so sánh).</summary>
    public Task<IReadOnlyList<TraceNode>> ForwardByRecursionAsync(string siteId, short rootType, string rootId,
        short? descendantType, CancellationToken cancellationToken) => QueryAsync("""
            WITH RECURSIVE down(node_type, node_id) AS (
                SELECT child_type, child_id FROM trace.genealogy_link
                WHERE site_id = @site AND parent_type = @type AND parent_id = @id
                  AND unlinked_at IS NULL AND edge_kind IN (1, 2, 4, 5)
                UNION
                SELECT l.child_type, l.child_id FROM down d
                JOIN trace.genealogy_link l ON l.site_id = @site AND l.parent_type = d.node_type
                    AND l.parent_id = d.node_id AND l.unlinked_at IS NULL AND l.edge_kind IN (1, 2, 4, 5)
            )
            SELECT node_type, node_id FROM down
            WHERE @filter::smallint IS NULL OR node_type = @filter::smallint
            ORDER BY node_type, node_id;
            """, siteId, rootType, rootId, descendantType, cancellationToken);

    /// <summary>Backward bằng recursive CTE trên bảng cạnh, không dùng closure (để so sánh).</summary>
    public Task<IReadOnlyList<TraceNode>> BackwardByRecursionAsync(string siteId, short rootType, string rootId,
        short? ancestorType, CancellationToken cancellationToken) => QueryAsync("""
            WITH RECURSIVE up(node_type, node_id) AS (
                SELECT parent_type, parent_id FROM trace.genealogy_link
                WHERE site_id = @site AND child_type = @type AND child_id = @id
                  AND unlinked_at IS NULL AND edge_kind IN (1, 2, 4, 5)
                UNION
                SELECT l.parent_type, l.parent_id FROM up u
                JOIN trace.genealogy_link l ON l.site_id = @site AND l.child_type = u.node_type
                    AND l.child_id = u.node_id AND l.unlinked_at IS NULL AND l.edge_kind IN (1, 2, 4, 5)
            )
            SELECT node_type, node_id FROM up
            WHERE @filter::smallint IS NULL OR node_type = @filter::smallint
            ORDER BY node_type, node_id;
            """, siteId, rootType, rootId, ancestorType, cancellationToken);

    /// <summary>Unit lấy vật liệu từ đoạn [fromMeter, toMeter) của cuộn; không trả cả cuộn.</summary>
    public async Task<IReadOnlyList<TraceNode>> RollSpanAsync(string siteId, string rollId, decimal fromMeter,
        decimal toMeter, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT DISTINCT child_type, child_id FROM trace.genealogy_link
            WHERE site_id = @site AND parent_type = 5 AND parent_id = @roll AND unlinked_at IS NULL
              AND span && numrange(@from, @to)
            ORDER BY child_type, child_id;
            """);
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("roll", rollId);
        command.Parameters.AddWithValue("from", fromMeter);
        command.Parameters.AddWithValue("to", toMeter);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mọi unit (cell, module, pack) hạ nguồn của một lot, một cuộn, một đoạn cuộn [from, to) hoặc một unit — tập
    /// mà hold cascade phải giữ. Với đoạn cuộn, chỉ cell lấy vật liệu từ đoạn đó và unit chứa chúng.
    /// </summary>
    public async Task<IReadOnlyList<string>> DownstreamUnitsAsync(string siteId, short rootType, string rootId,
        decimal? fromMeter, decimal? toMeter, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(fromMeter is not null && toMeter is not null
            ? """
              WITH cells AS (
                  SELECT DISTINCT child_id FROM trace.genealogy_link
                  WHERE site_id = @site AND parent_type = @type AND parent_id = @id AND unlinked_at IS NULL
                    AND child_type = 2 AND span && numrange(@from, @to))
              SELECT child_id FROM cells
              UNION
              SELECT c.descendant_id FROM rm.genealogy_closure c JOIN cells ON c.ancestor_id = cells.child_id
              WHERE c.site_id = @site AND c.ancestor_type = 2 AND c.descendant_type IN (3, 4);
              """
            : """
              SELECT descendant_id FROM rm.genealogy_closure
              WHERE site_id = @site AND ancestor_type = @type AND ancestor_id = @id AND descendant_type IN (2, 3, 4);
              """);
        command.CommandTimeout = 120;
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("type", rootType);
        command.Parameters.AddWithValue("id", rootId);
        command.Parameters.AddWithValue("from", (object?)fromMeter ?? DBNull.Value);
        command.Parameters.AddWithValue("to", (object?)toMeter ?? DBNull.Value);
        var serials = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { serials.Add(reader.GetString(0)); }
        return serials;
    }

    private async Task<IReadOnlyList<TraceNode>> QueryAsync(string sql, string siteId, short type, string id,
        short? filter, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("filter", NpgsqlTypes.NpgsqlDbType.Smallint)
        { Value = (object?)filter ?? DBNull.Value });
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TraceNode>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var nodes = new List<TraceNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { nodes.Add(new TraceNode(TypeName(reader.GetInt16(0)), reader.GetString(1))); }
        return nodes;
    }
}

/// <summary>HTTP của trace API; site lấy từ token đã xác thực.</summary>
public static class TraceEndpoints
{
    private const int MaxIdLength = 100;

    public static IEndpointRouteBuilder MapNvmTrace(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/v1/trace").RequireAuthorization(PomRegistration.ReadPolicy);
        group.MapGet("/forward/{type}/{id}", async (string type, string id, string? only, HttpContext context,
            TraceQueries queries, CancellationToken ct) =>
        {
            if (!Valid(type, id, only, out var root, out var filter))
            { return Results.BadRequest(); }
            var site = context.User.FindFirst("site_id")!.Value;
            return Results.Ok(new TraceResult(site, new TraceNode(type.ToLowerInvariant(), id),
                await queries.ForwardAsync(site, root, id, filter, ct)));
        });
        group.MapGet("/backward/{type}/{id}", async (string type, string id, string? only, HttpContext context,
            TraceQueries queries, CancellationToken ct) =>
        {
            if (!Valid(type, id, only, out var root, out var filter))
            { return Results.BadRequest(); }
            var site = context.User.FindFirst("site_id")!.Value;
            return Results.Ok(new TraceResult(site, new TraceNode(type.ToLowerInvariant(), id),
                await queries.BackwardAsync(site, root, id, filter, ct)));
        });
        group.MapGet("/roll/{rollId}/span", async (string rollId, decimal from, decimal to, HttpContext context,
            TraceQueries queries, CancellationToken ct) =>
        {
            if (rollId.Length > MaxIdLength || from < 0 || to <= from)
            { return Results.BadRequest(); }
            var site = context.User.FindFirst("site_id")!.Value;
            return Results.Ok(new TraceResult(site, new TraceNode("roll", rollId),
                await queries.RollSpanAsync(site, rollId, from, to, ct)));
        });
        return endpoints;
    }

    private static bool Valid(string type, string id, string? only, out short root, out short? filter)
    {
        filter = null;
        if (!TraceQueries.TryType(type, out root) || string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength)
        { return false; }
        if (only is null)
        { return true; }
        if (!TraceQueries.TryType(only, out var value))
        { return false; }
        filter = value;
        return true;
    }
}
