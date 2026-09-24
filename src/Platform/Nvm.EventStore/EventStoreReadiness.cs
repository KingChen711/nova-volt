using Microsoft.Data.SqlClient;

namespace Nvm.EventStore;

/// <summary>Checks required event store and dispatcher grants using the runtime principal.</summary>
public static class EventStoreReadiness
{
    public static async Task<bool> CheckAsync(SqlEventStoreOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = new SqlCommand("""
                SELECT CASE WHEN
                    HAS_PERMS_BY_NAME('es.Streams','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('es.Streams','OBJECT','INSERT')=1 AND
                    HAS_PERMS_BY_NAME('es.Streams','OBJECT','UPDATE','Version','COLUMN')=1 AND
                    HAS_PERMS_BY_NAME('es.Events','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('es.Events','OBJECT','INSERT')=1 AND
                    HAS_PERMS_BY_NAME('es.Snapshots','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('es.Snapshots','OBJECT','INSERT')=1 AND
                    HAS_PERMS_BY_NAME('es.Outbox','OBJECT','SELECT')=1 AND
                    HAS_PERMS_BY_NAME('es.Outbox','OBJECT','INSERT')=1 AND
                    HAS_PERMS_BY_NAME('es.Outbox','OBJECT','UPDATE','Attempt','COLUMN')=1 AND
                    HAS_PERMS_BY_NAME('es.Outbox','OBJECT','UPDATE','NextAttemptAt','COLUMN')=1 AND
                    HAS_PERMS_BY_NAME('es.Outbox','OBJECT','UPDATE','ClaimId','COLUMN')=1 AND
                    HAS_PERMS_BY_NAME('es.Outbox','OBJECT','UPDATE','LastError','COLUMN')=1 AND
                    HAS_PERMS_BY_NAME('es.Outbox','OBJECT','UPDATE','DispatchedAt','COLUMN')=1 AND
                    COL_LENGTH('es.Events','CloudEventJson') IS NOT NULL
                    THEN 1 ELSE 0 END;
                """, connection) { CommandTimeout = 3 };
            return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
        }
        catch (SqlException) { return false; }
    }
}
