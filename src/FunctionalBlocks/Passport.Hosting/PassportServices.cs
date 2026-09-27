using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
using Nvm.CommandStore;
using Nvm.Contracts.Queries;
using Nvm.Passport.Entities;
using Nvm.Passport.Handlers;
using Nvm.PublicObjectModel;

namespace Nvm.Passport.Hosting;

/// <summary>
/// Bằng chứng về pack: trạng thái unit từ traceability (cùng transaction command), tổ tiên trong genealogy (closure) và
/// recipe của các cuộn điện cực từ read model trace.
/// </summary>
public sealed class TracePackEvidenceSource(IUnitExecutionContextReader units, TraceQueries trace, NpgsqlDataSource readModel)
    : IPackEvidenceSource
{
    private const short PackType = 4;

    public async Task<(string UnitKind, PackEvidence Evidence)?> ReadAsync(string siteId, string serialNumber,
        CancellationToken cancellationToken)
    {
        var unit = await units.ReadForCommandAsync(serialNumber, cancellationToken).ConfigureAwait(false);
        if (unit is null || !string.Equals(unit.SiteId, siteId, StringComparison.Ordinal))
        { return null; }
        var ancestors = await trace.BackwardAsync(siteId, PackType, serialNumber, null, cancellationToken).ConfigureAwait(false);
        var rolls = ancestors.Where(n => n.Type == "roll").Select(n => n.Id).ToArray();
        var recipes = ImmutableArray.CreateBuilder<string>();
        if (rolls.Length > 0)
        {
            await using var command = readModel.CreateCommand("""
                SELECT DISTINCT recipe_version_id FROM trace.roll_segment WHERE site_id = @site AND roll_id = ANY(@rolls);
                """);
            command.Parameters.AddWithValue("site", siteId);
            command.Parameters.AddWithValue("rolls", rolls);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            { recipes.Add(reader.GetString(0)); }
        }
        return (unit.UnitKind, new PackEvidence(serialNumber, unit.ProductCode ?? "", unit.QualityState,
            [.. ancestors.Where(n => n.Type is "cell" or "module").Select(n => $"{n.Type}:{n.Id}")],
            [.. ancestors.Where(n => n.Type is "lot" or "roll").Select(n => $"{n.Type}:{n.Id}")],
            recipes.ToImmutable()));
    }
}

/// <summary>Kết quả resolver: passport đã lọc, hoặc lý do không trả.</summary>
public sealed record PassportReadResult(PassportView? View, int StatusCode);

/// <summary>
/// Đọc passport đã công bố theo GS1 Digital Link. Site lấy từ serial (ADR-007), không từ token: người đọc bên ngoài
/// không thuộc site nào. Lượt đọc của regulator được ghi audit trước khi trả dữ liệu; ghi audit lỗi thì không trả.
/// </summary>
public sealed class PassportReader(SqlCommandStoreOptions options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PassportReadResult> ReadAsync(string gtin, string serialNumber, int? version, string audience, bool isOwner,
        string subject, CancellationToken cancellationToken)
    {
        if (serialNumber.Length != 16 || !Audiences.All.Contains(audience))
        { return new PassportReadResult(null, 400); }
        var site = serialNumber[..3];
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (1) ContentJson, Gtin FROM passport.Passports
            WHERE SiteId = @site AND SerialNumber = @serial AND Status = 'Published' AND (@version IS NULL OR Version = @version)
            ORDER BY Version DESC;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = site;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
        command.Parameters.Add("@version", SqlDbType.Int).Value = (object?)version ?? DBNull.Value;
        PassportDocument? document = null;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && string.Equals(reader.GetString(1), gtin, StringComparison.Ordinal))
            { document = PassportDocument.FromJson(reader.GetString(0)); }
        }
        if (document is null)
        { return new PassportReadResult(null, 404); }
        var view = PassportProjection.View(document, audience, isOwner);
        if (audience == Audiences.Regulator)
        {
            using var audit = new SqlCommand("""
                INSERT INTO passport.ReadAudit (SiteId, SerialNumber, Version, Audience, Subject, FieldCount, ReadAt)
                VALUES (@site, @serial, @version, @audience, @subject, @fields, @at);
                """, connection);
            audit.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = site;
            audit.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
            audit.Parameters.Add("@version", SqlDbType.Int).Value = document.Version;
            audit.Parameters.Add("@audience", SqlDbType.VarChar, 20).Value = audience;
            audit.Parameters.Add("@subject", SqlDbType.NVarChar, 200).Value = subject;
            audit.Parameters.Add("@fields", SqlDbType.Int).Value = view.Fields.Count;
            audit.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = clock.GetUtcNow();
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return new PassportReadResult(view, 200);
    }

    public async Task<IReadOnlyList<(int Version, string Subject, DateTimeOffset ReadAt)>> AuditAsync(string siteId,
        string serialNumber, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT Version, Subject, ReadAt FROM passport.ReadAudit WHERE SiteId = @site AND SerialNumber = @serial ORDER BY AuditId;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
        var rows = new List<(int, string, DateTimeOffset)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false)));
        }
        return rows;
    }

    /// <summary>Carbon footprint mới nhất của sản phẩm tại site cho một năm (cả sản phẩm không có passport).</summary>
    public async Task<CarbonFootprint?> CarbonAsync(string siteId, string productCode, int year, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (1) Version, KgCo2ePerKwh, RecycledContentJson, VerifiedBy FROM passport.CarbonFootprints
            WHERE SiteId = @site AND ProductCode = @product AND Year = @year ORDER BY Version DESC;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = productCode;
        command.Parameters.Add("@year", SqlDbType.Int).Value = year;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CarbonFootprint(productCode, year, reader.GetInt32(0), reader.GetDecimal(1),
                JsonSerializer.Deserialize<ImmutableDictionary<string, decimal>>(reader.GetString(2), Json)!, reader.GetString(3))
            : null;
    }
}
