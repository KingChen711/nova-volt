using System.Data;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Equipment.Commands;
using Nvm.Equipment.Entities;
using Nvm.Equipment.Handlers;
using Nvm.Kernel.Commands;

namespace Nvm.Equipment.Hosting;

public sealed record RegisterEquipmentPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string EquipmentPath, [property: JsonRequired] string EquipmentClass);
public sealed record ChangeStatePayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string EquipmentPath,
    [property: JsonRequired] string State, string? ReasonCode);
public sealed record ProductionCountPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string EquipmentPath, [property: JsonRequired] string ProductCode,
    [property: JsonRequired] DateTimeOffset WindowFrom, [property: JsonRequired] DateTimeOffset WindowTo,
    [property: JsonRequired] long TotalCount, [property: JsonRequired] long GoodCount);
public sealed record IdealCyclePayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string EquipmentClass,
    [property: JsonRequired] string ProductCode, [property: JsonRequired] int CycleMilliseconds,
    [property: JsonRequired] DateTimeOffset EffectiveFrom);

/// <summary>OEE của một máy hoặc của cả nhóm: luôn kèm base để người đọc đối chiếu được bằng tay.</summary>
public sealed record OeeLine(string Scope, decimal PlannedSeconds, decimal RunSeconds, decimal UnplannedDowntimeSeconds,
    decimal MicroStopSeconds, decimal IdealSeconds, long TotalCount, long GoodCount, decimal? Availability,
    decimal? Performance, decimal? Quality, decimal? Oee)
{
    public static OeeLine From(string scope, OeeBase value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(scope, value.PlannedSeconds, value.RunSeconds, value.UnplannedDowntimeSeconds, value.MicroStopSeconds,
            value.IdealSeconds, value.TotalCount, value.GoodCount, value.Availability, value.Performance, value.Quality, value.Oee);
    }
}

public sealed record OeeReport(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<OeeLine> Equipment, OeeLine Combined);

public sealed record EquipmentStatus(string EquipmentPath, string EquipmentClass, string State, DateTimeOffset StateSince,
    string? ReasonCode);

/// <summary>Đọc OEE và trạng thái máy của đúng một site. Gộp bằng cách cộng base (scope §6.10).</summary>
public sealed class OeeQueries(SqlCommandStoreOptions options, TimeProvider clock)
{
    public async Task<OeeReport> CalculateAsync(string siteId, IReadOnlyCollection<string> equipmentPaths, DateTimeOffset from,
        DateTimeOffset to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(equipmentPaths);
        var now = clock.GetUtcNow();
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var machines = (await StatusAsync(connection, siteId, cancellationToken).ConfigureAwait(false))
            .Where(m => equipmentPaths.Count == 0 || equipmentPaths.Contains(m.EquipmentPath)).ToList();
        var downtimes = machines.ToDictionary(m => m.EquipmentPath, _ => new List<DowntimeInterval>(), StringComparer.Ordinal);
        var counts = machines.ToDictionary(m => m.EquipmentPath, _ => new List<ProductionCount>(), StringComparer.Ordinal);
        using (var command = new SqlCommand("""
            SELECT EquipmentPath, StartedAt, EndedAt, Category FROM equipment.Downtimes
            WHERE SiteId = @site AND StartedAt < @to AND EndedAt > @from;
            SELECT EquipmentPath, WindowFrom, WindowTo, TotalCount, GoodCount, IdealCycleMilliseconds FROM equipment.ProductionCounts
            WHERE SiteId = @site AND WindowTo > @from AND WindowTo <= @to;
            SELECT e.EquipmentPath, e.StateSince, r.Category FROM equipment.Equipment e
            JOIN equipment.DowntimeReasons r ON r.SiteId = e.SiteId AND r.Code = e.ReasonCode
            WHERE e.SiteId = @site AND e.State = 'Stopped' AND e.StateSince < @to;
            """, connection))
        {
            command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
            command.Parameters.Add("@from", SqlDbType.DateTimeOffset).Value = from;
            command.Parameters.Add("@to", SqlDbType.DateTimeOffset).Value = to;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (downtimes.TryGetValue(reader.GetString(0), out var list))
                {
                    list.Add(new DowntimeInterval(await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false),
                        await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false), reader.GetString(3)));
                }
            }
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (counts.TryGetValue(reader.GetString(0), out var list))
                {
                    list.Add(new ProductionCount(await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false),
                        await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false),
                        reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5)));
                }
            }
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Lần dừng đang mở: tính tới min(to, now) và phân loại theo độ dài tới lúc này.
                if (!downtimes.TryGetValue(reader.GetString(0), out var list))
                { continue; }
                var started = await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false);
                var end = now < to ? now : to;
                if (end > started)
                { list.Add(new DowntimeInterval(started, end, DowntimeRules.Classify(reader.GetString(2), end - started))); }
            }
        }
        var lines = machines.Select(m => (m.EquipmentPath,
            Base: OeeCalculator.For(from, to, downtimes[m.EquipmentPath], counts[m.EquipmentPath]))).ToList();
        return new OeeReport(from, to, [.. lines.Select(l => OeeLine.From(l.EquipmentPath, l.Base))],
            OeeLine.From("combined", OeeBase.Combine(lines.Select(l => l.Base))));
    }

    public async Task<IReadOnlyList<EquipmentStatus>> StatusAsync(string siteId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await StatusAsync(connection, siteId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DowntimeReason>> ReasonTreeAsync(string siteId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT Code, ParentCode, Category, IsLeaf FROM equipment.DowntimeReasons WHERE SiteId = @site ORDER BY Code;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        var rows = new List<DowntimeReason>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DowntimeReason(reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetBoolean(3)));
        }
        return rows;
    }

    private static async Task<List<EquipmentStatus>> StatusAsync(SqlConnection connection, string siteId,
        CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("""
            SELECT EquipmentPath, EquipmentClass, State, StateSince, ReasonCode FROM equipment.Equipment
            WHERE SiteId = @site ORDER BY EquipmentPath;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        var rows = new List<EquipmentStatus>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new EquipmentStatus(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4)));
        }
        return rows;
    }
}

public static class EquipmentRegistration
{
    public const string WritePolicy = "EquipmentWrite";
    public const string MasterDataPolicy = "EquipmentMasterData";
    public const string ReadPolicy = "EquipmentRead";

    public static IServiceCollection AddNvmEquipment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IEquipmentStore, SqlEquipmentStore>();
        services.TryAddSingleton<OeeQueries>();
        services.AddSiteWritePolicy(WritePolicy, "Operator", "LineLeader");
        services.AddSiteWritePolicy(MasterDataPolicy, "ProductionManager", "Admin");
        services.AddSiteWritePolicy(ReadPolicy, "Operator", "LineLeader", "QaEngineer", "QaManager", "ProductionManager");
        return services;
    }

    public static IEndpointRouteBuilder MapNvmEquipment(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/equipment/register", (CommandRequest<RegisterEquipmentPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new RegisterEquipmentCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.EquipmentPath, input.Payload.EquipmentClass), ct))
            .RequireAuthorization(MasterDataPolicy);
        endpoints.MapPost("/api/v1/commands/equipment/change-state", (CommandRequest<ChangeStatePayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ChangeEquipmentStateCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.EquipmentPath, input.Payload.State,
                input.Payload.ReasonCode), ct))
            .RequireAuthorization(WritePolicy);
        endpoints.MapPost("/api/v1/commands/equipment/record-count", (CommandRequest<ProductionCountPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new RecordProductionCountCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.EquipmentPath, input.Payload.ProductCode,
                input.Payload.WindowFrom, input.Payload.WindowTo, input.Payload.TotalCount, input.Payload.GoodCount), ct))
            .RequireAuthorization(WritePolicy);
        endpoints.MapPost("/api/v1/commands/equipment/set-ideal-cycle", (CommandRequest<IdealCyclePayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new SetIdealCycleTimeCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.EquipmentClass, input.Payload.ProductCode,
                input.Payload.CycleMilliseconds, input.Payload.EffectiveFrom), ct))
            .RequireAuthorization(MasterDataPolicy);
        endpoints.MapGet("/api/v1/oee", async (DateTimeOffset from, DateTimeOffset to, [FromQuery] string[]? equipment,
            HttpContext context, OeeQueries queries, CancellationToken ct) =>
            to <= from || to - from > TimeSpan.FromDays(92) ? Results.BadRequest()
                : Results.Ok(await queries.CalculateAsync(Site(context), equipment ?? [], from, to, ct)))
            .RequireAuthorization(ReadPolicy);
        endpoints.MapGet("/api/v1/equipment", async (HttpContext context, OeeQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.StatusAsync(Site(context), ct))).RequireAuthorization(ReadPolicy);
        endpoints.MapGet("/api/v1/equipment/downtime-reasons", async (HttpContext context, OeeQueries queries,
            CancellationToken ct) => Results.Ok(await queries.ReasonTreeAsync(Site(context), ct))).RequireAuthorization(ReadPolicy);
        return endpoints;
    }

    private static string Site(HttpContext context) => context.User.FindFirst("site_id")!.Value;

    private static Task<IResult> Execute<TPayload>(CommandRequest<TPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs, Func<string, string, CommandRequest<TPayload>, DurableCommand> create,
        CancellationToken ct) where TPayload : class =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.Equipment"), create,
            reason => reason switch
            {
                EquipmentReasonCodes.EquipmentNotFound or EquipmentReasonCodes.NoIdealCycle => 404,
                EquipmentReasonCodes.UnknownReason or EquipmentReasonCodes.ReasonNotLeaf => 400,
                _ => 409
            }, ct);
}

/// <summary>Migration tường minh của Equipment.</summary>
public static class EquipmentSchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(EquipmentSchemaMigrator).Assembly, "Nvm.Equipment.Hosting.Migrations.",
            connectionString, cancellationToken);
}
