using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace Nvm.Projections;

/// <summary>Explicit read-model migration; callers run it as a deployment step.</summary>
public static class ProjectionSchemaMigrator
{
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "SQL comes from a fixed embedded migration resource.")]
    public static async Task UpgradeAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var assembly = typeof(ProjectionSchemaMigrator).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var resource = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Projection migration resource is missing.");
            using var reader = new StreamReader(resource);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
