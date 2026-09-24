using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace Nvm.Projections;

/// <summary>Explicitly switches the POM query views from fixture rows to live unit projections.</summary>
public static class ProjectionPomConnector
{
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The SQL is a fixed embedded deployment resource.")]
    public static async Task ConnectAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        using var stream = typeof(ProjectionPomConnector).Assembly.GetManifestResourceStream(
            "Nvm.Projections.Deployment.connect-pom.sql") ?? throw new InvalidOperationException("POM deployment resource missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
