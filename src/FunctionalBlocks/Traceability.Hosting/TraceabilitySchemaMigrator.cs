using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace Nvm.Traceability.Hosting;

/// <summary>Explicit upgrade using the schema owner credential, outside request processing.</summary>
public static class TraceabilitySchemaMigrator
{
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Migration SQL is an embedded resource, never user input.")]
    public static async Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        using var stream = typeof(TraceabilitySchemaMigrator).Assembly.GetManifestResourceStream(
            "Nvm.Traceability.Hosting.Migrations.001-traceability.sql")
            ?? throw new InvalidOperationException("Traceability migration resource is missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
