using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Passport.Entities;
using Nvm.Passport.Handlers;

namespace Nvm.Passport.Hosting;

/// <summary>Passport trên SQL, trong transaction của command đang giữ claim.</summary>
public sealed class SqlPassportStore(SqlCommandSession session) : IPassportStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ModelColumns = """
        Gtin, ProductCode, Manufacturer, Chemistry, NominalEnergyKwh, ExpectedLifetimeCycles, RequiresPassport,
        MaterialCompositionJson, DismantlingUri, SafetyUri
        """;

    public Task<BatteryModel?> ModelAsync(string siteId, string gtin, CancellationToken cancellationToken) =>
        ModelWhereAsync(siteId, "Gtin = @key", gtin, cancellationToken);

    public Task<BatteryModel?> ModelForProductAsync(string siteId, string productCode, CancellationToken cancellationToken) =>
        ModelWhereAsync(siteId, "ProductCode = @key", productCode, cancellationToken);

    public async Task AddModelAsync(string siteId, BatteryModel model, string actorId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        Require(siteId);
        using var command = session.CreateCommand($"""
            INSERT INTO passport.BatteryModels (SiteId, {ModelColumns}, DefinedBy, DefinedAt)
            VALUES (@site, @gtin, @product, @manufacturer, @chemistry, @energy, @cycles, @requires, @composition, @dismantling,
                @safety, @actor, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@gtin", SqlDbType.Char, 14).Value = model.Gtin;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = model.ProductCode;
        command.Parameters.Add("@manufacturer", SqlDbType.NVarChar, 200).Value = model.Manufacturer;
        command.Parameters.Add("@chemistry", SqlDbType.NVarChar, 50).Value = model.Chemistry;
        command.Parameters.Add("@energy", SqlDbType.Decimal).Value = model.NominalEnergyKwh;
        command.Parameters["@energy"].Precision = 9;
        command.Parameters["@energy"].Scale = 3;
        command.Parameters.Add("@cycles", SqlDbType.Int).Value = model.ExpectedLifetimeCycles;
        command.Parameters.Add("@requires", SqlDbType.Bit).Value = model.RequiresPassport;
        command.Parameters.Add("@composition", SqlDbType.NVarChar, -1).Value = model.MaterialCompositionJson;
        command.Parameters.Add("@dismantling", SqlDbType.NVarChar, 500).Value = model.DismantlingUri;
        command.Parameters.Add("@safety", SqlDbType.NVarChar, 500).Value = model.SafetyUri;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CarbonFootprint?> CarbonAsync(string siteId, string productCode, int year, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT TOP (1) Version, KgCo2ePerKwh, RecycledContentJson, VerifiedBy FROM passport.CarbonFootprints
            WHERE SiteId = @site AND ProductCode = @product AND Year = @year ORDER BY Version DESC;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = productCode;
        command.Parameters.Add("@year", SqlDbType.Int).Value = year;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new CarbonFootprint(productCode, year, reader.GetInt32(0), reader.GetDecimal(1),
            JsonSerializer.Deserialize<ImmutableDictionary<string, decimal>>(reader.GetString(2), Json)!, reader.GetString(3));
    }

    public async Task<int> AddCarbonAsync(string siteId, CarbonFootprint carbon, string actorId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carbon);
        Require(siteId);
        using var command = session.CreateCommand("""
            DECLARE @version int = 1 + ISNULL((SELECT MAX(Version) FROM passport.CarbonFootprints WITH (UPDLOCK, HOLDLOCK)
                WHERE SiteId = @site AND ProductCode = @product AND Year = @year), 0);
            INSERT INTO passport.CarbonFootprints (SiteId, ProductCode, Year, Version, KgCo2ePerKwh, RecycledContentJson, VerifiedBy,
                RecordedBy, RecordedAt)
            VALUES (@site, @product, @year, @version, @carbon, @recycled, @verified, @actor, @at);
            SELECT @version;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = carbon.ProductCode;
        command.Parameters.Add("@year", SqlDbType.Int).Value = carbon.Year;
        var value = command.Parameters.Add("@carbon", SqlDbType.Decimal);
        value.Precision = 12;
        value.Scale = 4;
        value.Value = carbon.KgCo2ePerKwh;
        command.Parameters.Add("@recycled", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(
            carbon.RecycledContentPercent.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value), Json);
        command.Parameters.Add("@verified", SqlDbType.NVarChar, 200).Value = carbon.VerifiedBy;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<StoredPassport?> LatestForUpdateAsync(string siteId, string serialNumber, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT TOP (1) ContentJson, ContentSha256, Status, PreparedBy FROM passport.Passports WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND SerialNumber = @serial ORDER BY Version DESC;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
        return await ReadStoredAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int?> LatestPublishedVersionAsync(string siteId, string serialNumber, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT MAX(Version) FROM passport.Passports WHERE SiteId = @site AND SerialNumber = @serial AND Status = 'Published';
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int version ? version : null;
    }

    public async Task<StoredPassport?> LoadForUpdateAsync(string siteId, string serialNumber, int version,
        CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT ContentJson, ContentSha256, Status, PreparedBy FROM passport.Passports WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND SerialNumber = @serial AND Version = @version;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
        command.Parameters.Add("@version", SqlDbType.Int).Value = version;
        return await ReadStoredAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddDraftAsync(string siteId, PassportDocument document, string sha256, string actorId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO passport.Passports (SiteId, SerialNumber, Version, Gtin, PreviousVersion, ContentJson, ContentSha256, Status,
                PreparedBy, PreparedAt)
            VALUES (@site, @serial, @version, @gtin, @previous, @content, @sha, 'Draft', @actor, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = document.SerialNumber;
        command.Parameters.Add("@version", SqlDbType.Int).Value = document.Version;
        command.Parameters.Add("@gtin", SqlDbType.Char, 14).Value = document.Gtin;
        command.Parameters.Add("@previous", SqlDbType.Int).Value = (object?)document.PreviousVersion ?? DBNull.Value;
        command.Parameters.Add("@content", SqlDbType.NVarChar, -1).Value = document.ToJson();
        command.Parameters.Add("@sha", SqlDbType.Char, 64).Value = sha256;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(string siteId, string serialNumber, int version, ImmutableArray<string> signatureIds,
        string actorId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE passport.Passports SET Status = 'Published', SignatureIdsJson = @signatures, PublishedBy = @actor, PublishedAt = @at
            WHERE SiteId = @site AND SerialNumber = @serial AND Version = @version AND Status = 'Draft';
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
        command.Parameters.Add("@version", SqlDbType.Int).Value = version;
        command.Parameters.Add("@signatures", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(signatureIds, Json);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException($"Passport {serialNumber} v{version} is not a draft."); }
    }

    private async Task<BatteryModel?> ModelWhereAsync(string siteId, string predicate, string key, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand($"SELECT {ModelColumns} FROM passport.BatteryModels WHERE SiteId = @site AND {predicate};");
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@key", SqlDbType.NVarChar, 100).Value = key;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new BatteryModel(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDecimal(4), reader.GetInt32(5), reader.GetBoolean(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9))
            : null;
    }

    private static async Task<StoredPassport?> ReadStoredAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredPassport(PassportDocument.FromJson(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3))
            : null;
    }

    private void Require(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Passport site does not match the active command transaction."); }
    }
}
