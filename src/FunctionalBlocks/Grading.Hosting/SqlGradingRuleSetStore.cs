using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Contracts.Events.Grading;
using Nvm.Grading.Entities;
using Nvm.Grading.Handlers;

namespace Nvm.Grading.Hosting;

/// <summary>Rule set grading trên SQL, trong transaction của command đang giữ claim.</summary>
public sealed class SqlGradingRuleSetStore(SqlCommandSession session) : IGradingRuleSetStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Definition(ImmutableArray<GradingBin> Bins, ImmutableArray<GradingReject> Rejects);

    public async Task<StoredRuleSet?> LoadForUpdateAsync(string siteId, string ruleSetId, int version,
        CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            SELECT ProductCode, EffectiveFrom, DefinitionJson, Status, AuthoredBy FROM grading.RuleSets WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND RuleSetId = @id AND Version = @version;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 50).Value = ruleSetId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = version;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new StoredRuleSet(Read(ruleSetId, version, reader.GetString(0),
            await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false), reader.GetString(2)),
            reader.GetString(3), reader.GetString(4));
    }

    public async Task SaveDraftAsync(string siteId, GradingRuleSet ruleSet, string author, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO grading.RuleSets (SiteId, RuleSetId, Version, ProductCode, EffectiveFrom, DefinitionJson, Status,
                AuthoredBy, AuthoredAt)
            VALUES (@site, @id, @version, @product, @effective, @definition, 'Draft', @author, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 50).Value = ruleSet.RuleSetId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = ruleSet.Version;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = ruleSet.ProductCode;
        command.Parameters.Add("@effective", SqlDbType.DateTimeOffset).Value = ruleSet.EffectiveFrom;
        command.Parameters.Add("@definition", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(new Definition(ruleSet.Bins, ruleSet.Rejects), Json);
        command.Parameters.Add("@author", SqlDbType.NVarChar, 200).Value = author;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ApproveAsync(string siteId, GradingRuleSet ruleSet, string approver, string sha256,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            UPDATE grading.RuleSets SET Status = 'Approved', ApprovedBy = @approver, ApprovedAt = @at, ContentSha256 = @sha
            WHERE SiteId = @site AND RuleSetId = @id AND Version = @version AND Status = 'Draft';
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 50).Value = ruleSet.RuleSetId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = ruleSet.Version;
        command.Parameters.Add("@approver", SqlDbType.NVarChar, 200).Value = approver;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        command.Parameters.Add("@sha", SqlDbType.Char, 64).Value = sha256;
        try
        { return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1; }
        catch (SqlException error) when (error.Number is 2601 or 2627)
        { return false; }
    }

    public async Task<GradingRuleSet?> FindEffectiveAsync(string siteId, string productCode, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            SELECT TOP (1) RuleSetId, Version, EffectiveFrom, DefinitionJson FROM grading.RuleSets WITH (HOLDLOCK)
            WHERE SiteId = @site AND ProductCode = @product AND Status = 'Approved' AND EffectiveFrom <= @at
            ORDER BY EffectiveFrom DESC;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = productCode;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return Read(reader.GetString(0), reader.GetInt32(1), productCode,
            await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false), reader.GetString(3));
    }

    private static GradingRuleSet Read(string id, int version, string product, DateTimeOffset effective, string json)
    {
        var definition = JsonSerializer.Deserialize<Definition>(json, Json)
            ?? throw new InvalidDataException("Stored rule set definition is null.");
        return new GradingRuleSet(id, version, product, effective, definition.Bins,
            definition.Rejects.IsDefault ? [] : definition.Rejects);
    }

    private void RequireSite(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Grading site does not match the active command transaction."); }
    }
}
