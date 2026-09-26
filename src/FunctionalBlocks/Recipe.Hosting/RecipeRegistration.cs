using System.Collections.Immutable;
using System.Data;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Contracts.Events.Recipe;
using Nvm.Kernel.Commands;
using Nvm.Recipe.Commands;
using Nvm.Recipe.Entities;
using Nvm.Recipe.Handlers;

namespace Nvm.Recipe.Hosting;

public sealed record DefineRecipePayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string RecipeId,
    [property: JsonRequired] int Version, [property: JsonRequired] string ProductCode, [property: JsonRequired] string StepCode,
    [property: JsonRequired] string EquipmentClass, [property: JsonRequired] ImmutableArray<RecipeParameter> Parameters);
public sealed record ApproveRecipePayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string RecipeId,
    [property: JsonRequired] int Version, [property: JsonRequired] DateTimeOffset EffectiveFrom,
    [property: JsonRequired] ImmutableArray<string> SignatureIds);
public sealed record ApplyRecipePayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string EquipmentPath,
    [property: JsonRequired] string EquipmentClass, [property: JsonRequired] string ProductCode,
    [property: JsonRequired] string StepCode, [property: JsonRequired] string OperationRunId, string? LotId);

/// <summary>Recipe đã chạy trên một máy tại một thời điểm.</summary>
public sealed record AppliedRecipe(string EquipmentPath, DateTimeOffset AppliedAt, string RecipeId, int Version,
    string ContentSha256, string OperationRunId, string? LotId);

/// <summary>Trả lời "lúc T, máy X chạy recipe nào" từ bảng append-only của các lần apply.</summary>
public sealed class RecipeQueries(SqlCommandStoreOptions options)
{
    public async Task<AppliedRecipe?> AppliedAtAsync(string siteId, string equipmentPath, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (1) AppliedAt, RecipeId, Version, ContentSha256, OperationRunId, LotId FROM recipe.Applications
            WHERE SiteId = @site AND EquipmentPath = @equipment AND AppliedAt <= @at ORDER BY AppliedAt DESC;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@equipment", SqlDbType.NVarChar, 200).Value = equipmentPath;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new AppliedRecipe(equipmentPath, await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken).ConfigureAwait(false),
            reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5));
    }
}

public static class RecipeRegistration
{
    public const string AuthorPolicy = "RecipeAuthor";
    public const string ApplyPolicy = "RecipeApply";
    public const string ReadPolicy = "RecipeRead";

    public static IServiceCollection AddNvmRecipe(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IRecipeStore, SqlRecipeStore>();
        services.TryAddSingleton<RecipeQueries>();
        services.AddSiteWritePolicy(AuthorPolicy, "QaEngineer", "QaManager", "ProductionManager");
        services.AddSiteWritePolicy(ApplyPolicy, "Operator", "LineLeader");
        services.AddSiteWritePolicy(ReadPolicy, "Operator", "LineLeader", "QaEngineer", "QaManager", "ProductionManager");
        return services;
    }

    public static IEndpointRouteBuilder MapNvmRecipe(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/recipe/define", (CommandRequest<DefineRecipePayload>? request, HttpContext context,
            ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new DefineRecipeVersionCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.RecipeId, input.Payload.Version,
                input.Payload.ProductCode, input.Payload.StepCode, input.Payload.EquipmentClass, input.Payload.Parameters), ct))
            .RequireAuthorization(AuthorPolicy);
        endpoints.MapPost("/api/v1/commands/recipe/approve", (CommandRequest<ApproveRecipePayload>? request, HttpContext context,
            ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ApproveRecipeVersionCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.RecipeId, input.Payload.Version,
                input.Payload.EffectiveFrom, input.Payload.SignatureIds), ct))
            .RequireAuthorization(AuthorPolicy);
        endpoints.MapPost("/api/v1/commands/recipe/apply", (CommandRequest<ApplyRecipePayload>? request, HttpContext context,
            ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ApplyRecipeCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.EquipmentPath, input.Payload.EquipmentClass,
                input.Payload.ProductCode, input.Payload.StepCode, input.Payload.OperationRunId, input.Payload.LotId), ct))
            .RequireAuthorization(ApplyPolicy);
        endpoints.MapGet("/api/v1/recipes/applied", async (string equipmentPath, DateTimeOffset at, HttpContext context,
            RecipeQueries queries, CancellationToken ct) => equipmentPath.Length is 0 or > 200 ? Results.BadRequest()
                : await queries.AppliedAtAsync(context.User.FindFirst("site_id")!.Value, equipmentPath, at, ct) is { } applied
                    ? Results.Ok(applied) : Results.NotFound())
            .RequireAuthorization(ReadPolicy);
        return endpoints;
    }

    private static Task<IResult> Execute<TPayload>(CommandRequest<TPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs, Func<string, string, CommandRequest<TPayload>, DurableCommand> create,
        CancellationToken ct) where TPayload : class =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.Recipe"), create,
            reason => reason switch
            {
                RecipeReasonCodes.RecipeNotFound or RecipeReasonCodes.NoActiveRecipe => 404,
                RecipeReasonCodes.InvalidRecipe => 400,
                "SEPARATION_OF_DUTIES" => 403,
                _ => 409
            }, ct);
}

/// <summary>Migration tường minh của Recipe.</summary>
public static class RecipeSchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(RecipeSchemaMigrator).Assembly, "Nvm.Recipe.Hosting.Migrations.",
            connectionString, cancellationToken);
}
