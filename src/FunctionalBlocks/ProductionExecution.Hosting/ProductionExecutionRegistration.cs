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
        return endpoints;
    }

    private static Task<IResult> RecordRollCoatedAsync(CommandRequest<RecordRollCoatedPayload>? request,
        HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.ProductionExecution"),
            (site, actor, input) => new RecordRollCoatedCommand(site, actor, input.Payload.SubmissionId,
                input.OccurredAt, input.Payload.RollId, input.Payload.Segments),
            reason => reason == RollReasonCodes.OverlappingSegments ? 400 : 409, ct);
}
