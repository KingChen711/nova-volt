using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Nvm.Ingestion;

internal sealed class PostgresReadinessCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", exception);
        }
    }
}
