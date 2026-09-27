using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace Nvm.EventStore;

/// <summary>Explicit SQL migration, never invoked while serving requests.</summary>
public static class EventSchemaMigrator
{
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Migration SQL is an embedded resource, never user input.")]
    public static async Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Mọi file Migrations/NNN-*.sql theo thứ tự tên, trong một transaction; mỗi file tự chạy lại được.
        var assembly = typeof(EventSchemaMigrator).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Nvm.EventStore.Migrations.", StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (scripts.Count == 0 || !scripts[0].EndsWith("001-event-store.sql", StringComparison.Ordinal))
        { throw new InvalidOperationException("Event store migration resource is missing."); }
        foreach (var script in scripts)
        {
            using var stream = assembly.GetManifestResourceStream(script)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var command = new SqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
