using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Nvm.Projections;

/// <summary>Checks the inbox/read-model schema and the runtime role's required permissions.</summary>
public sealed class ProjectionStoreHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT has_schema_privilege(current_user, 'rm', 'USAGE')
                    AND bool_and(has_table_privilege(current_user, required.table_name, required.privilege))
                    AND has_column_privilege(current_user, 'rm.unit_projection_inbox', 'applied', 'UPDATE')
                FROM (VALUES
                    ('rm.unit_current', 'SELECT'), ('rm.unit_current', 'INSERT'), ('rm.unit_current', 'UPDATE'),
                    ('rm.projection_checkpoint', 'SELECT'), ('rm.projection_checkpoint', 'INSERT'),
                    ('rm.projection_checkpoint', 'UPDATE'), ('rm.unit_projection_inbox', 'SELECT'),
                    ('rm.unit_projection_inbox', 'INSERT')) AS required(table_name, privilege);
                """);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Projection schema permissions are incomplete.");
        }
        catch (PostgresException error)
        { return HealthCheckResult.Unhealthy($"Projection store check failed (SQLSTATE {error.SqlState})."); }
        catch (NpgsqlException)
        { return HealthCheckResult.Unhealthy("Projection store is unavailable or not migrated."); }
    }
}
