using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.ProductionExecution.Entities;
using Nvm.ProductionExecution.Ports;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Trạng thái formation/aging và timeout, trong transaction của command đang giữ claim.</summary>
public sealed class SqlFormationProcessStore(SqlCommandSession session) : IFormationProcessStore
{
    public async Task<FormationAgingProcess?> LoadForUpdateAsync(string siteId, string serialNumber,
        CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            SELECT State, Version, TrayId, Channel, EquipmentPath, FormationDueAt, CapacityAh, Ocv1Millivolt,
                RackId, Level, AgingChannel, AgingDueAt, Ocv2Millivolt
            FROM execution.FormationAging WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND SerialNumber = @serial;
            """);
        AddIdentity(command, siteId, serialNumber);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new FormationAgingProcess(siteId, serialNumber,
            Enum.Parse<FormationAgingState>(reader.GetString(0), ignoreCase: false), reader.GetInt64(1),
            reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
            await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken).ConfigureAwait(false),
            await ValueAsync<decimal>(reader, 6, cancellationToken).ConfigureAwait(false),
            await ValueAsync<decimal>(reader, 7, cancellationToken).ConfigureAwait(false),
            await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(8),
            await ValueAsync<int>(reader, 9, cancellationToken).ConfigureAwait(false),
            await ValueAsync<int>(reader, 10, cancellationToken).ConfigureAwait(false),
            await ValueAsync<DateTimeOffset>(reader, 11, cancellationToken).ConfigureAwait(false),
            await ValueAsync<decimal>(reader, 12, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<T?> ValueAsync<T>(SqlDataReader reader, int ordinal, CancellationToken cancellationToken)
        where T : struct =>
        await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? null : await reader.GetFieldValueAsync<T>(ordinal, cancellationToken).ConfigureAwait(false);

    public async Task SaveAsync(FormationAgingProcess process, bool isNew, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        RequireSite(process.SiteId);
        using var command = session.CreateCommand(isNew
            ? """
              INSERT INTO execution.FormationAging (SiteId, SerialNumber, State, Version, TrayId, Channel, EquipmentPath,
                  FormationDueAt, CapacityAh, Ocv1Millivolt, Ocv2Millivolt, RackId, Level, AgingChannel, AgingDueAt, UpdatedAt)
              VALUES (@site, @serial, @state, @version, @tray, @channel, @equipment, @formationDue, @capacity, @ocv1,
                  @ocv2, @rack, @level, @agingChannel, @agingDue, @at);
              """
            : """
              UPDATE execution.FormationAging SET State = @state, Version = @version, CapacityAh = @capacity,
                  Ocv1Millivolt = @ocv1, Ocv2Millivolt = @ocv2, RackId = @rack, Level = @level,
                  AgingChannel = @agingChannel, AgingDueAt = @agingDue, UpdatedAt = @at
              WHERE SiteId = @site AND SerialNumber = @serial AND Version < @version;
              """);
        AddIdentity(command, process.SiteId, process.SerialNumber);
        command.Parameters.Add("@state", SqlDbType.VarChar, 30).Value = process.State.ToString();
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = process.Version;
        command.Parameters.Add("@tray", SqlDbType.NVarChar, 50).Value = process.TrayId;
        command.Parameters.Add("@channel", SqlDbType.Int).Value = process.Channel;
        command.Parameters.Add("@equipment", SqlDbType.NVarChar, 200).Value = process.EquipmentPath;
        command.Parameters.Add("@formationDue", SqlDbType.DateTimeOffset).Value = process.FormationDueAt;
        Add(command, "@capacity", SqlDbType.Decimal, process.CapacityAh, 9, 4);
        Add(command, "@ocv1", SqlDbType.Decimal, process.Ocv1Millivolt, 9, 3);
        Add(command, "@ocv2", SqlDbType.Decimal, process.Ocv2Millivolt, 9, 3);
        command.Parameters.Add("@rack", SqlDbType.NVarChar, 20).Value = (object?)process.RackId ?? DBNull.Value;
        command.Parameters.Add("@level", SqlDbType.Int).Value = (object?)process.Level ?? DBNull.Value;
        command.Parameters.Add("@agingChannel", SqlDbType.Int).Value = (object?)process.AgingChannel ?? DBNull.Value;
        command.Parameters.Add("@agingDue", SqlDbType.DateTimeOffset).Value = (object?)process.AgingDueAt ?? DBNull.Value;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException("Formation process changed concurrently."); }
    }

    public async Task ScheduleAsync(string siteId, string serialNumber, string kind, DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO execution.ProcessTimeouts (SiteId, SerialNumber, Kind, DueAt) VALUES (@site, @serial, @kind, @due);
            """);
        AddIdentity(command, siteId, serialNumber);
        command.Parameters.Add("@kind", SqlDbType.VarChar, 30).Value = kind;
        command.Parameters.Add("@due", SqlDbType.DateTimeOffset).Value = dueAt;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CompleteTimeoutAsync(string siteId, string serialNumber, string kind, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            UPDATE execution.ProcessTimeouts SET CompletedAt = @at
            WHERE SiteId = @site AND SerialNumber = @serial AND Kind = @kind AND CompletedAt IS NULL;
            """);
        AddIdentity(command, siteId, serialNumber);
        command.Parameters.Add("@kind", SqlDbType.VarChar, 30).Value = kind;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void Add(SqlCommand command, string name, SqlDbType type, decimal? value, byte precision, byte scale)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private void RequireSite(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Formation site does not match the active command transaction."); }
    }

    private static void AddIdentity(SqlCommand command, string siteId, string serialNumber)
    {
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
    }
}

/// <summary>Đọc timeout đến hạn bằng connection riêng, không khoá; việc bắn đi qua command có claim.</summary>
public sealed class SqlDueTimeoutSource(SqlCommandStoreOptions options) : IDueTimeoutSource
{
    public async Task<IReadOnlyList<DueTimeout>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (@limit) SiteId, SerialNumber, Kind, DueAt FROM execution.ProcessTimeouts WITH (READPAST)
            WHERE CompletedAt IS NULL AND DueAt <= @now ORDER BY DueAt;
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@now", SqlDbType.DateTimeOffset).Value = now;
        var due = new List<DueTimeout>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            due.Add(new DueTimeout(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false)));
        }
        return due;
    }
}
