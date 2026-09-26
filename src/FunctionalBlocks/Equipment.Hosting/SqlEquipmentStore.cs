using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Contracts.Events.Equipment;
using Nvm.Equipment.Entities;
using Nvm.Equipment.Handlers;

namespace Nvm.Equipment.Hosting;

/// <summary>Máy và OEE trên SQL, trong transaction của command đang giữ claim.</summary>
public sealed class SqlEquipmentStore(SqlCommandSession session) : IEquipmentStore
{
    public async Task<EquipmentState?> LoadForUpdateAsync(string siteId, string equipmentPath, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT EquipmentClass, State, StateSince, ReasonCode, StreamVersion FROM equipment.Equipment WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND EquipmentPath = @path;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@path", SqlDbType.NVarChar, 200).Value = equipmentPath;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new EquipmentState(equipmentPath, reader.GetString(0), reader.GetString(1),
            await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
            reader.GetInt64(4));
    }

    public async Task RegisterAsync(string siteId, EquipmentState state, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO equipment.Equipment (SiteId, EquipmentPath, EquipmentClass, State, StateSince, ReasonCode, StreamVersion, UpdatedAt)
            VALUES (@site, @path, @class, @state, @since, @reason, @version, @at);
            """);
        AddState(command, siteId, state, at);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateStateAsync(string siteId, EquipmentState state, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE equipment.Equipment SET State = @state, StateSince = @since, ReasonCode = @reason, StreamVersion = @version,
                UpdatedAt = @at
            WHERE SiteId = @site AND EquipmentPath = @path AND EquipmentClass = @class;
            """);
        AddState(command, siteId, state, at);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException($"Equipment {state.EquipmentPath} changed class or disappeared inside a transaction."); }
    }

    public async Task<DowntimeReason?> ReasonAsync(string siteId, string reasonCode, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT Code, ParentCode, Category, IsLeaf FROM equipment.DowntimeReasons WHERE SiteId = @site AND Code = @code;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@code", SqlDbType.VarChar, 40).Value = reasonCode;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new DowntimeReason(reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
            reader.GetString(2), reader.GetBoolean(3));
    }

    public async Task AddDowntimeAsync(string siteId, EquipmentDowntimeRecorded downtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downtime);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO equipment.Downtimes (SiteId, EventId, EquipmentPath, StartedAt, EndedAt, ReasonCode, Category)
            VALUES (@site, @event, @path, @started, @ended, @reason, @category);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = downtime.EventId;
        command.Parameters.Add("@path", SqlDbType.NVarChar, 200).Value = downtime.EquipmentPath;
        command.Parameters.Add("@started", SqlDbType.DateTimeOffset).Value = downtime.StartedAt;
        command.Parameters.Add("@ended", SqlDbType.DateTimeOffset).Value = downtime.EndedAt;
        command.Parameters.Add("@reason", SqlDbType.VarChar, 40).Value = downtime.ReasonCode;
        command.Parameters.Add("@category", SqlDbType.VarChar, 10).Value = downtime.Category;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int Version, int CycleMilliseconds)?> IdealCycleAtAsync(string siteId, string equipmentClass,
        string productCode, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT TOP (1) Version, CycleMilliseconds FROM equipment.IdealCycleTimes
            WHERE SiteId = @site AND EquipmentClass = @class AND ProductCode = @product AND EffectiveFrom <= @at
            ORDER BY EffectiveFrom DESC, Version DESC;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@class", SqlDbType.NVarChar, 50).Value = equipmentClass;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = productCode;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetInt32(0), reader.GetInt32(1)) : null;
    }

    public async Task<int> AddIdealCycleAsync(string siteId, string equipmentClass, string productCode, int cycleMilliseconds,
        DateTimeOffset effectiveFrom, string actorId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            DECLARE @version int = 1 + ISNULL((SELECT MAX(Version) FROM equipment.IdealCycleTimes WITH (UPDLOCK, HOLDLOCK)
                WHERE SiteId = @site AND EquipmentClass = @class AND ProductCode = @product), 0);
            INSERT INTO equipment.IdealCycleTimes (SiteId, EquipmentClass, ProductCode, Version, CycleMilliseconds, EffectiveFrom,
                SetBy, SetAt)
            VALUES (@site, @class, @product, @version, @cycle, @from, @actor, @at);
            SELECT @version;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@class", SqlDbType.NVarChar, 50).Value = equipmentClass;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = productCode;
        command.Parameters.Add("@cycle", SqlDbType.Int).Value = cycleMilliseconds;
        command.Parameters.Add("@from", SqlDbType.DateTimeOffset).Value = effectiveFrom;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<bool> AddCountAsync(string siteId, ProductionCountRecorded count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(count);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO equipment.ProductionCounts (SiteId, EventId, EquipmentPath, ProductCode, WindowFrom, WindowTo, TotalCount,
                GoodCount, IdealCycleMilliseconds, IdealCycleVersion)
            SELECT @site, @event, @path, @product, @from, @to, @total, @good, @cycle, @cycleVersion
            WHERE NOT EXISTS (SELECT 1 FROM equipment.ProductionCounts WITH (UPDLOCK, HOLDLOCK)
                              WHERE SiteId = @site AND EquipmentPath = @path AND WindowFrom = @from);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = count.EventId;
        command.Parameters.Add("@path", SqlDbType.NVarChar, 200).Value = count.EquipmentPath;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = count.ProductCode;
        command.Parameters.Add("@from", SqlDbType.DateTimeOffset).Value = count.WindowFrom;
        command.Parameters.Add("@to", SqlDbType.DateTimeOffset).Value = count.WindowTo;
        command.Parameters.Add("@total", SqlDbType.BigInt).Value = count.TotalCount;
        command.Parameters.Add("@good", SqlDbType.BigInt).Value = count.GoodCount;
        command.Parameters.Add("@cycle", SqlDbType.Int).Value = count.IdealCycleMilliseconds;
        command.Parameters.Add("@cycleVersion", SqlDbType.Int).Value = count.IdealCycleVersion;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddState(SqlCommand command, string siteId, EquipmentState state, DateTimeOffset at)
    {
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@path", SqlDbType.NVarChar, 200).Value = state.EquipmentPath;
        command.Parameters.Add("@class", SqlDbType.NVarChar, 50).Value = state.EquipmentClass;
        command.Parameters.Add("@state", SqlDbType.VarChar, 10).Value = state.State;
        command.Parameters.Add("@since", SqlDbType.DateTimeOffset).Value = state.StateSince;
        command.Parameters.Add("@reason", SqlDbType.VarChar, 40).Value = (object?)state.ReasonCode ?? DBNull.Value;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = state.StreamVersion;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
    }

    private void Require(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Equipment site does not match the active command transaction."); }
    }
}
