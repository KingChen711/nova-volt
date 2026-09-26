using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Contracts.Events.Recipe;
using Nvm.Recipe.Entities;
using Nvm.Recipe.Handlers;

namespace Nvm.Recipe.Hosting;

/// <summary>Recipe trên SQL, trong transaction của command đang giữ claim.</summary>
public sealed class SqlRecipeStore(SqlCommandSession session) : IRecipeStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Columns =
        "RecipeId, Version, ProductCode, StepCode, EquipmentClass, ParametersJson, Status, EffectiveFrom, EffectiveTo, AuthoredBy";

    public async Task<StoredRecipe?> LoadForUpdateAsync(string siteId, string recipeId, int version,
        CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand($"""
            SELECT {Columns} FROM recipe.RecipeVersions WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND RecipeId = @id AND Version = @version;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 50).Value = recipeId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = version;
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task SaveDraftAsync(string siteId, RecipeVersion recipe, string author, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO recipe.RecipeVersions (SiteId, RecipeId, Version, ProductCode, StepCode, EquipmentClass, ParametersJson,
                Status, AuthoredBy, AuthoredAt)
            VALUES (@site, @id, @version, @product, @step, @class, @parameters, 'Draft', @author, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 50).Value = recipe.RecipeId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = recipe.Version;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = recipe.ProductCode;
        command.Parameters.Add("@step", SqlDbType.NVarChar, 20).Value = recipe.StepCode;
        command.Parameters.Add("@class", SqlDbType.NVarChar, 50).Value = recipe.EquipmentClass;
        command.Parameters.Add("@parameters", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(recipe.Parameters, Json);
        command.Parameters.Add("@author", SqlDbType.NVarChar, 200).Value = author;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecipeVersion?> ActiveForUpdateAsync(string siteId, string equipmentClass, string productCode,
        string stepCode, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand($"""
            SELECT {Columns} FROM recipe.RecipeVersions WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND EquipmentClass = @class AND ProductCode = @product AND StepCode = @step AND Status = 'Active';
            """);
        AddKey(command, siteId, equipmentClass, productCode, stepCode);
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0].Recipe;
    }

    public async Task<bool> ActivateAsync(string siteId, RecipeVersion recipe, RecipeVersion? previous,
        DateTimeOffset effectiveFrom, string approver, string sha256, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE recipe.RecipeVersions SET Status = 'Superseded', EffectiveTo = @from
            WHERE SiteId = @site AND RecipeId = @previousId AND Version = @previousVersion AND Status = 'Active';
            UPDATE recipe.RecipeVersions SET Status = 'Active', EffectiveFrom = @from, ContentSha256 = @sha,
                ApprovedBy = @approver, ApprovedAt = @at
            WHERE SiteId = @site AND RecipeId = @id AND Version = @version AND Status = 'Draft';
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@previousId", SqlDbType.VarChar, 50).Value = (object?)previous?.RecipeId ?? DBNull.Value;
        command.Parameters.Add("@previousVersion", SqlDbType.Int).Value = (object?)previous?.Version ?? DBNull.Value;
        command.Parameters.Add("@id", SqlDbType.VarChar, 50).Value = recipe.RecipeId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = recipe.Version;
        command.Parameters.Add("@from", SqlDbType.DateTimeOffset).Value = effectiveFrom;
        command.Parameters.Add("@sha", SqlDbType.Char, 64).Value = sha256;
        command.Parameters.Add("@approver", SqlDbType.NVarChar, 200).Value = approver;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        try
        { return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) >= 1; }
        catch (SqlException error) when (error.Number is 2601 or 2627)
        { return false; }
    }

    public async Task<RecipeVersion?> EffectiveAtAsync(string siteId, string equipmentClass, string productCode,
        string stepCode, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand($"""
            SELECT TOP (1) {Columns} FROM recipe.RecipeVersions WITH (HOLDLOCK)
            WHERE SiteId = @site AND EquipmentClass = @class AND ProductCode = @product AND StepCode = @step
              AND Status IN ('Active', 'Superseded') AND EffectiveFrom <= @at AND (EffectiveTo IS NULL OR EffectiveTo > @at)
            ORDER BY EffectiveFrom DESC;
            """);
        AddKey(command, siteId, equipmentClass, productCode, stepCode);
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0].Recipe;
    }

    public async Task RecordApplicationAsync(string siteId, RecipeVersionApplied applied, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applied);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO recipe.Applications (SiteId, EventId, EquipmentPath, AppliedAt, RecipeId, Version, ContentSha256,
                OperationRunId, LotId)
            VALUES (@site, @event, @equipment, @at, @id, @version, @sha, @run, @lot);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = applied.EventId;
        command.Parameters.Add("@equipment", SqlDbType.NVarChar, 200).Value = applied.EquipmentPath;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = applied.OccurredAt;
        command.Parameters.Add("@id", SqlDbType.VarChar, 50).Value = applied.RecipeId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = applied.Version;
        command.Parameters.Add("@sha", SqlDbType.Char, 64).Value = applied.ContentSha256;
        command.Parameters.Add("@run", SqlDbType.NVarChar, 100).Value = applied.OperationRunId;
        command.Parameters.Add("@lot", SqlDbType.NVarChar, 100).Value = (object?)applied.LotId ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddKey(SqlCommand command, string siteId, string equipmentClass, string productCode, string stepCode)
    {
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@class", SqlDbType.NVarChar, 50).Value = equipmentClass;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = productCode;
        command.Parameters.Add("@step", SqlDbType.NVarChar, 20).Value = stepCode;
    }

    private static async Task<List<StoredRecipe>> ReadAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<StoredRecipe>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var parameters = JsonSerializer.Deserialize<ImmutableArray<RecipeParameter>>(reader.GetString(5), Json);
            rows.Add(new StoredRecipe(new RecipeVersion(reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), parameters, Enum.Parse<RecipeStatus>(reader.GetString(6)),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null
                    : await reader.GetFieldValueAsync<DateTimeOffset>(7, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null
                    : await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellationToken).ConfigureAwait(false)),
                reader.GetString(9)));
        }
        return rows;
    }

    private void Require(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Recipe site does not match the active command transaction."); }
    }
}
