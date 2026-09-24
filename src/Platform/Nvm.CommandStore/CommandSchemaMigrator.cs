using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace Nvm.CommandStore;

/// <summary>Migration tường minh và idempotent; không chạy trong startup phục vụ request.</summary>
public static class CommandSchemaMigrator
{
    /// <summary>Áp dụng schema bằng credential migration riêng.</summary>
    /// <remarks>SQL đọc từ embedded resource của chính assembly này, không có input người dùng.</remarks>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Script migration là embedded resource của assembly, không có input người dùng.")]
    public static async Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var migration in new[] { "001-command-store.sql", "002-data-collection.sql" })
        {
            using var stream = typeof(CommandSchemaMigrator).Assembly.GetManifestResourceStream("Nvm.CommandStore.Migrations." + migration)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var command = new SqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
