using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nvm.CommandStore;

/// <summary>Readiness kiểm schema và quyền bằng principal runtime, không tự migrate.</summary>
public static class CommandStoreReadiness
{
    /// <summary>Trả false khi schema hoặc quyền không sẵn sàng; không đưa chi tiết connection ra HTTP.</summary>
    public static async Task<bool> CheckAsync(SqlCommandStoreOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        { return false; }
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = new SqlCommand("""
                SELECT CASE WHEN
                    HAS_PERMS_BY_NAME('command_store.CommandOutcomes','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('command_store.CommandOutcomes','OBJECT','INSERT')=1 AND
                    HAS_PERMS_BY_NAME('command_store.CommandOutcomes','OBJECT','UPDATE')=1 AND
                    HAS_PERMS_BY_NAME('execution.UnitContext','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('execution.DataCollection','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('execution.DataCollection','OBJECT','INSERT')=1
                    THEN 1 ELSE 0 END;
                """, connection) { CommandTimeout = 3 };
            return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
        }
        catch (SqlException) { return false; }
    }
}

/// <summary>Adapter readiness cho Host.All.</summary>
public sealed class CommandStoreHealthCheck(SqlCommandStoreOptions options) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => await CommandStoreReadiness.CheckAsync(options, cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Command store schema or permissions unavailable");
}
