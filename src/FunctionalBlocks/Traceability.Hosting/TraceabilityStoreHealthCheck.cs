using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nvm.CommandStore;
using Nvm.EventStore;

namespace Nvm.Traceability.Hosting;

/// <summary>Both hosts use the same readiness contract for the deployed command dependencies.</summary>
public sealed class TraceabilityStoreHealthCheck(SqlCommandStoreOptions commands, SqlEventStoreOptions events)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => await EventStoreReadiness.CheckAsync(events, cancellationToken).ConfigureAwait(false)
            && await CheckAsync(commands, cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Event or traceability store schema/permissions unavailable");

    public static async Task<bool> CheckAsync(SqlCommandStoreOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = new SqlCommand("""
                SELECT CASE WHEN
                    HAS_PERMS_BY_NAME('traceability.Routes','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('traceability.ActorRoles','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('traceability.SerialReservations','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('traceability.SerialReservations','OBJECT','INSERT')=1 AND
                    HAS_PERMS_BY_NAME('traceability.SerialReservations','OBJECT','UPDATE')=1 AND
                    HAS_PERMS_BY_NAME('traceability.DuplicateSerialIncidents','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('traceability.DuplicateSerialIncidents','OBJECT','INSERT')=1
                    THEN 1 ELSE 0 END;
                """, connection) { CommandTimeout = 3 };
            return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
        }
        catch (SqlException) { return false; }
    }
}
