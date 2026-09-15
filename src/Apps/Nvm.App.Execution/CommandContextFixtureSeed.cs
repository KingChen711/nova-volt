using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;

namespace Nvm.App.Execution;

/// <summary>Seed context SQL từ chính generator C03; chỉ gọi từ lệnh Development riêng.</summary>
public static class CommandContextFixtureSeed
{
    /// <summary>Chèn những serial chưa tồn tại; không ghi đè state của context đã có.</summary>
    public static async Task<int> PrepareAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await CommandSchemaMigrator.UpgradeAsync(connectionString, cancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var inserted = 0;
        foreach (var unit in OperatorFixture.GenerateUnits())
        {
            using var command = new SqlCommand("""
                INSERT INTO execution.UnitContext(SiteId, SerialNumber, ContextJson)
                SELECT @site, @serial, @json
                WHERE NOT EXISTS (SELECT 1 FROM execution.UnitContext WITH (UPDLOCK, HOLDLOCK)
                    WHERE SiteId = @site AND SerialNumber = @serial);
                """, connection, transaction);
            command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = unit.SiteId;
            command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = unit.SerialNumber;
            command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(unit);
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }
}
