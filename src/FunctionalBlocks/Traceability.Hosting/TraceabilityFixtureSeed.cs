using System.Data;
using Microsoft.Data.SqlClient;

namespace Nvm.Traceability.Hosting;

/// <summary>Development/demo route; invoke explicitly from the host's Development fixture command.</summary>
public static class TraceabilityFixtureSeed
{
    public const string ProductCode = "NV-CELL-DEMO";
    public const string RoutingVersion = "r1";

    /// <summary>Insert missing demo routes without changing a route already used by serialized units.</summary>
    public static async Task<int> PrepareAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        await TraceabilitySchemaMigrator.UpgradeAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var inserted = 0;
        foreach (var site in new[] { "NV1", "DE1" })
        {
            using var command = new SqlCommand("""
                INSERT INTO traceability.Routes
                    (SiteId, ProductCode, RoutingVersion, StepsJson, TransitionsJson)
                SELECT @site, @product, @version, @steps, @transitions
                WHERE NOT EXISTS (
                    SELECT 1 FROM traceability.Routes WITH (UPDLOCK, HOLDLOCK)
                    WHERE SiteId = @site AND ProductCode = @product AND RoutingVersion = @version);
                """, connection, transaction);
            command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = site;
            command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = ProductCode;
            command.Parameters.Add("@version", SqlDbType.NVarChar, 50).Value = RoutingVersion;
            command.Parameters.Add("@steps", SqlDbType.NVarChar, -1).Value = """
                [{"code":"STACK"},{"code":"TABWELD"},{"code":"FORMATION"},{"code":"GRADING"}]
                """;
            command.Parameters.Add("@transitions", SqlDbType.NVarChar, -1).Value = """
                [{"action":"StartStep","from":0,"to":1},
                 {"action":"CompleteStep","from":1,"to":2},
                 {"action":"StartStep","from":2,"to":1}]
                """;
            inserted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }
}
