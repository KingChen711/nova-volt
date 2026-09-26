using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Kernel.Commands;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Ports;

namespace Nvm.ProductionExecution.Hosting;

public sealed record RecordRollCoatedPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string RollId, [property: JsonRequired] ImmutableArray<RollSegment> Segments);

public sealed record StartFormationPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] string TrayId,
    [property: JsonRequired] int Channel, [property: JsonRequired] string EquipmentPath);
public sealed record CompleteFormationPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] decimal CapacityAh, string? CurveUri);
public sealed record StartAgingPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] decimal Ocv1Millivolt,
    [property: JsonRequired] string RackId, [property: JsonRequired] int Level, [property: JsonRequired] int Channel);
public sealed record RecordOcv2Payload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] decimal Ocv2Millivolt);

/// <summary>Cùng adapter và policy cho Execution và Host.All.</summary>
public static class ProductionExecutionRegistration
{
    public const string RecordPolicy = "ProductionRecord";

    public static IServiceCollection AddNvmProductionExecutionAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddScoped<IProductionContextSource, SqlProductionContextSource>();
        services.AddScoped<IDataCollectionStore, SqlDataCollectionStore>();
        services.AddScoped<IFormationProcessStore, SqlFormationProcessStore>();
        services.AddSingleton<IDueTimeoutSource, SqlDueTimeoutSource>();
        services.AddSingleton<FormationTimeoutWorker>();
        services.AddSingleton<AgingWarehouseQueries>();
        if (!string.Equals(configuration["NVM_FORMATION:TimeoutWorker"], "false", StringComparison.OrdinalIgnoreCase))
        { services.AddHostedService(provider => provider.GetRequiredService<FormationTimeoutWorker>()); }
        services.AddAuthorizationBuilder().AddPolicy(RecordPolicy, policy => policy
            .RequireAuthenticatedUser().RequireRole("Operator", "LineLeader")
            .RequireAssertion(context =>
            {
                var sites = context.User.FindAll("site_id").ToArray();
                var subjects = context.User.FindAll("sub").ToArray();
                return sites.Length == 1 && sites[0].Value is "NV1" or "DE1"
                    && subjects.Length == 1 && !string.IsNullOrWhiteSpace(subjects[0].Value)
                    && subjects[0].Value.Length <= 200;
            }));
        return services;
    }

    public static IEndpointRouteBuilder MapNvmProductionExecution(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/production/record-data-collection", RecordDataCollectionEndpoint.HandleAsync)
            .RequireAuthorization(RecordPolicy);
        endpoints.MapPost("/api/v1/commands/production/record-roll-coated", RecordRollCoatedAsync)
            .RequireAuthorization(RecordPolicy);
        AgingWarehouseQueries.MapAgingWarehouse(endpoints, RecordPolicy);
        endpoints.MapPost("/api/v1/commands/production/start-formation", (CommandRequest<StartFormationPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new StartFormationCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.SerialNumber, input.Payload.TrayId,
                input.Payload.Channel, input.Payload.EquipmentPath), ct)).RequireAuthorization(RecordPolicy);
        endpoints.MapPost("/api/v1/commands/production/complete-formation", (CommandRequest<CompleteFormationPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new CompleteFormationCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.SerialNumber, input.Payload.CapacityAh,
                input.Payload.CurveUri), ct)).RequireAuthorization(RecordPolicy);
        endpoints.MapPost("/api/v1/commands/production/start-aging", (CommandRequest<StartAgingPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new StartAgingCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.SerialNumber, input.Payload.Ocv1Millivolt,
                input.Payload.RackId, input.Payload.Level, input.Payload.Channel), ct)).RequireAuthorization(RecordPolicy);
        endpoints.MapPost("/api/v1/commands/production/record-ocv2", (CommandRequest<RecordOcv2Payload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new RecordOcv2Command(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.SerialNumber, input.Payload.Ocv2Millivolt), ct))
            .RequireAuthorization(RecordPolicy);
        return endpoints;
    }

    private static Task<IResult> Execute<TPayload>(CommandRequest<TPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs,
        Func<string, string, CommandRequest<TPayload>, DurableCommand> create, CancellationToken ct) where TPayload : class =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.ProductionExecution"),
            create, reason => reason switch
            {
                Entities.FormationReasonCodes.UnitNotFound or Entities.FormationReasonCodes.ProcessNotFound => 404,
                _ => 409
            }, ct);

    private static Task<IResult> RecordRollCoatedAsync(CommandRequest<RecordRollCoatedPayload>? request,
        HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.ProductionExecution"),
            (site, actor, input) => new RecordRollCoatedCommand(site, actor, input.Payload.SubmissionId,
                input.OccurredAt, input.Payload.RollId, input.Payload.Segments),
            reason => reason == RollReasonCodes.OverlappingSegments ? 400 : 409, ct);
}
