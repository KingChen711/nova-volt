using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Material.Entities;
using Nvm.Material.Handlers;

namespace Nvm.Material.Hosting;

/// <summary>Lot vật liệu trên SQL, trong transaction của command đang giữ claim.</summary>
public sealed class SqlMaterialLotStore(SqlCommandSession session) : IMaterialLotStore
{
    private const string Columns =
        "LotId, MaterialCode, Remaining, UnitOfMeasure, ReceivedAt, ExpiresAt, MaxExposureMinutes, OpenedAt, Quality, StreamVersion";

    public async Task<MaterialLot?> LoadForUpdateAsync(string siteId, string lotId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand($"""
            SELECT {Columns} FROM material.Lots WITH (UPDLOCK, HOLDLOCK) WHERE SiteId = @site AND LotId = @lot;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@lot", SqlDbType.NVarChar, 100).Value = lotId;
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAsync(string siteId, MaterialLot lot, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lot);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO material.Lots (SiteId, LotId, MaterialCode, Remaining, UnitOfMeasure, ReceivedAt, ExpiresAt,
                MaxExposureMinutes, OpenedAt, Quality, StreamVersion, UpdatedAt)
            VALUES (@site, @lot, @material, @remaining, @uom, @received, @expires, @exposure, @opened, @quality, @version, @at);
            """);
        Bind(command, siteId, lot, at);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(string siteId, MaterialLot lot, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lot);
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE material.Lots SET Remaining = @remaining, OpenedAt = @opened, Quality = @quality,
                StreamVersion = @version, UpdatedAt = @at
            WHERE SiteId = @site AND LotId = @lot;
            """);
        Bind(command, siteId, lot, at);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException("Material lot disappeared during the command."); }
    }

    public async Task<MaterialLot?> OlderAvailableAsync(string siteId, MaterialLot lot, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lot);
        Require(siteId);
        using var command = session.CreateCommand($"""
            SELECT TOP (1) {Columns} FROM material.Lots
            WHERE SiteId = @site AND MaterialCode = @material AND ReceivedAt < @received AND LotId <> @lot
              AND Quality = 'Released' AND Remaining > 0 AND (ExpiresAt IS NULL OR ExpiresAt > @now)
            ORDER BY ReceivedAt;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@material", SqlDbType.NVarChar, 100).Value = lot.MaterialCode;
        command.Parameters.Add("@received", SqlDbType.DateTimeOffset).Value = lot.ReceivedAt;
        command.Parameters.Add("@lot", SqlDbType.NVarChar, 100).Value = lot.LotId;
        command.Parameters.Add("@now", SqlDbType.DateTimeOffset).Value = now;
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MaterialOverride>> OverridesAsync(string siteId, string lotId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT OverrideId, RuleCode, ValidUntil FROM material.Overrides WHERE SiteId = @site AND LotId = @lot;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@lot", SqlDbType.NVarChar, 100).Value = lotId;
        var result = new List<MaterialOverride>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MaterialOverride(reader.GetString(0), lotId, reader.GetString(1),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false)));
        }
        return result;
    }

    public async Task AddOverrideAsync(string siteId, MaterialOverride grant, string actorId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO material.Overrides (SiteId, OverrideId, LotId, RuleCode, ValidUntil, GrantedBy, GrantedAt)
            VALUES (@site, @id, @lot, @rule, @until, @by, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = grant.OverrideId;
        command.Parameters.Add("@lot", SqlDbType.NVarChar, 100).Value = grant.LotId;
        command.Parameters.Add("@rule", SqlDbType.VarChar, 32).Value = grant.Rule;
        command.Parameters.Add("@until", SqlDbType.DateTimeOffset).Value = grant.ValidUntil;
        command.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(SqlCommand command, string siteId, MaterialLot lot, DateTimeOffset at)
    {
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@lot", SqlDbType.NVarChar, 100).Value = lot.LotId;
        command.Parameters.Add("@material", SqlDbType.NVarChar, 100).Value = lot.MaterialCode;
        var remaining = command.Parameters.Add("@remaining", SqlDbType.Decimal);
        remaining.Precision = 18;
        remaining.Scale = 6;
        remaining.Value = lot.Remaining;
        command.Parameters.Add("@uom", SqlDbType.NVarChar, 20).Value = lot.UnitOfMeasure;
        command.Parameters.Add("@received", SqlDbType.DateTimeOffset).Value = lot.ReceivedAt;
        command.Parameters.Add("@expires", SqlDbType.DateTimeOffset).Value = (object?)lot.ExpiresAt ?? DBNull.Value;
        command.Parameters.Add("@exposure", SqlDbType.Int).Value = (object?)lot.MaxExposureMinutes ?? DBNull.Value;
        command.Parameters.Add("@opened", SqlDbType.DateTimeOffset).Value = (object?)lot.OpenedAt ?? DBNull.Value;
        command.Parameters.Add("@quality", SqlDbType.VarChar, 10).Value = lot.Quality.ToString();
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = lot.StreamVersion;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
    }

    private static async Task<MaterialLot?> ReadOneAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new MaterialLot(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3),
            await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken).ConfigureAwait(false),
            await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken).ConfigureAwait(false),
            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(6),
            await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(7, cancellationToken).ConfigureAwait(false),
            Enum.Parse<LotQuality>(reader.GetString(8)), reader.GetInt64(9));
    }

    private void Require(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Material site does not match the active command transaction."); }
    }
}

/// <summary>Migration tường minh của Material.</summary>
public static class MaterialSchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(MaterialSchemaMigrator).Assembly, "Nvm.Material.Hosting.Migrations.",
            connectionString, cancellationToken);
}
