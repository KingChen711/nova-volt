using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Nvm.CommandStore;

/// <summary>Chạy mọi script SQL nhúng theo thứ tự tên trong một transaction. Script phải idempotent.</summary>
public static class EmbeddedSqlMigrations
{
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Script là embedded resource của assembly gọi, không có input người dùng.")]
    public static async Task RunAsync(Assembly assembly, string resourcePrefix, string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (scripts.Length == 0)
        { throw new InvalidOperationException($"No migration resource starts with {resourcePrefix}."); }
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var script in scripts)
        {
            await using var stream = assembly.GetManifestResourceStream(script)
                ?? throw new InvalidOperationException($"Migration resource {script} is missing.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
