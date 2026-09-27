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
using Nvm.Contracts.Ports;
using Nvm.Kernel.Commands;
using Nvm.MasterData.Commands;
using Nvm.MasterData.Entities;
using Nvm.MasterData.Handlers;

namespace Nvm.MasterData.Hosting;

public sealed record DefineItemPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string Kind,
    [property: JsonRequired] string CanonicalId, [property: JsonRequired] string Name, string? BaseUom);
public sealed record MapAliasPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string Kind,
    [property: JsonRequired] string ExternalCode, [property: JsonRequired] string CanonicalId, [property: JsonRequired] string Reason);
public sealed record AcceptTaskPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string TaskId,
    [property: JsonRequired] string Note);

/// <summary>Một task đối soát cho màn hình Reconciliation.</summary>
public sealed record ReconciliationTaskView(string TaskId, string IssueKind, string ExternalCode, string? CanonicalId,
    string? ReceivedUom, string? ExpectedUom, string SourceDocument, string Detail, string Status, string? Resolution,
    DateTimeOffset OpenedAt);

public sealed record AliasView(string Kind, string ExternalCode, string CanonicalId, string Reason, string MappedBy,
    DateTimeOffset MappedAt);

/// <summary>Đọc master data của đúng một site, ngoài transaction command.</summary>
public sealed class MasterDataQueries(SqlCommandStoreOptions options)
{
    public async Task<long> RevisionAsync(string siteId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("SELECT Revision FROM masterdata.Revisions WHERE SiteId = @site;", connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision ? revision : 0;
    }

    public async Task<IReadOnlyList<ReconciliationTaskView>> TasksAsync(string siteId, string? status,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (1000) TaskId, IssueKind, ExternalCode, CanonicalId, ReceivedUom, ExpectedUom, SourceDocument, Detail,
                Status, Resolution, OpenedAt
            FROM masterdata.ReconciliationTasks WHERE SiteId = @site AND (@status IS NULL OR Status = @status)
            ORDER BY OpenedAt, TaskId;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@status", SqlDbType.VarChar, 10).Value = (object?)status ?? DBNull.Value;
        var rows = new List<ReconciliationTaskView>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            async Task<string?> Optional(int index) =>
                await reader.IsDBNullAsync(index, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(index);
            rows.Add(new ReconciliationTaskView(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                await Optional(3).ConfigureAwait(false), await Optional(4).ConfigureAwait(false),
                await Optional(5).ConfigureAwait(false), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                await Optional(9).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(10, cancellationToken).ConfigureAwait(false)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<AliasView>> AliasesAsync(string siteId, string kind, string canonicalId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT ExternalCode, Reason, MappedBy, MappedAt FROM masterdata.IdentityAliases
            WHERE SiteId = @site AND Kind = @kind AND CanonicalId = @id ORDER BY ExternalCode;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 10).Value = kind;
        command.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = IdentityCode.Normalize(canonicalId);
        var rows = new List<AliasView>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AliasView(kind, reader.GetString(0), IdentityCode.Normalize(canonicalId), reader.GetString(1),
                reader.GetString(2), await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false)));
        }
        return rows;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

public static class MasterDataRegistration
{
    public const string ManagePolicy = "MasterDataManage";
    public const string ReadPolicy = "MasterDataRead";

    public static IServiceCollection AddNvmMasterData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IMasterDataStore, SqlMasterDataStore>();
        services.TryAddScoped<IMasterDataReconciliation, MasterDataReconciliation>();
        services.TryAddSingleton<MasterDataQueries>();
        services.AddSiteWritePolicy(ManagePolicy, "ProductionManager", "Admin");
        services.AddSiteWritePolicy(ReadPolicy, "LineLeader", "QaEngineer", "QaManager", "ProductionManager", "Admin");
        return services;
    }

    public static IEndpointRouteBuilder MapNvmMasterData(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/masterdata/define-item", (CommandRequest<DefineItemPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new DefineCanonicalItemCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.Kind, input.Payload.CanonicalId, input.Payload.Name,
                input.Payload.BaseUom), ct)).RequireAuthorization(ManagePolicy);
        endpoints.MapPost("/api/v1/commands/masterdata/map-alias", (CommandRequest<MapAliasPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new MapIdentityAliasCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.Kind, input.Payload.ExternalCode,
                input.Payload.CanonicalId, input.Payload.Reason), ct)).RequireAuthorization(ManagePolicy);
        endpoints.MapPost("/api/v1/commands/masterdata/accept-task", (CommandRequest<AcceptTaskPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new AcceptReconciliationTaskCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.TaskId, input.Payload.Note), ct))
            .RequireAuthorization(ManagePolicy);
        endpoints.MapGet("/api/v1/masterdata/reconciliation-tasks", async (string? status, HttpContext context,
            MasterDataQueries queries, CancellationToken ct) => status is not (null or TaskStatuses.Open or TaskStatuses.Resolved)
                ? Results.BadRequest() : Results.Ok(await queries.TasksAsync(Site(context), status, ct)))
            .RequireAuthorization(ReadPolicy);
        endpoints.MapGet("/api/v1/masterdata/aliases/{kind}/{canonicalId}", async (string kind, string canonicalId,
            HttpContext context, MasterDataQueries queries, CancellationToken ct) =>
            kind is not (IdentityKinds.Material or IdentityKinds.Product or IdentityKinds.Equipment) || canonicalId.Length > 100
                ? Results.BadRequest() : Results.Ok(await queries.AliasesAsync(Site(context), kind, canonicalId, ct)))
            .RequireAuthorization(ReadPolicy);
        return endpoints;
    }

    private static string Site(HttpContext context) => context.User.FindFirst("site_id")!.Value;

    private static Task<IResult> Execute<TPayload>(CommandRequest<TPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs, Func<string, string, CommandRequest<TPayload>, DurableCommand> create,
        CancellationToken ct) where TPayload : class =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.MasterData"), create,
            reason => reason switch
            {
                MasterDataReasonCodes.ItemNotFound or MasterDataReasonCodes.TaskNotFound => 404,
                _ => 409
            }, ct);
}

/// <summary>Migration tường minh của MasterData.</summary>
public static class MasterDataSchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(MasterDataSchemaMigrator).Assembly, "Nvm.MasterData.Hosting.Migrations.",
            connectionString, cancellationToken);
}
