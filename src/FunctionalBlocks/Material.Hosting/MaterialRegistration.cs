using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.Material.Commands;
using Nvm.Material.Handlers;

namespace Nvm.Material.Hosting;

public sealed record ConsumeMaterialPayload(
    [property: JsonRequired] string SubmissionId, [property: JsonRequired] string ConsumerSerialNumber,
    [property: JsonRequired] string LotId, [property: JsonRequired] string LotKind,
    [property: JsonRequired] string MaterialCode, [property: JsonRequired] decimal Quantity,
    [property: JsonRequired] string UnitOfMeasure, decimal? SpanFromMeter, decimal? SpanToMeter,
    [property: JsonRequired] string OperationRunId);

public static class MaterialRegistration
{
    public const string WritePolicy = "MaterialWrite";

    /// <summary>Đăng ký sau AddNvmCommandStore và event store; dùng chung session của command.</summary>
    public static IServiceCollection AddNvmMaterial(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ICommandHandler<ConsumeMaterialCommand, DomainCommandResult>, ConsumeMaterialHandler>();
        services.TryAddScoped<ICommandValidator<ConsumeMaterialCommand>, ConsumeMaterialValidator>();
        services.AddSiteWritePolicy(WritePolicy, "Operator", "LineLeader");
        return services;
    }

    public static IEndpointRouteBuilder MapNvmMaterial(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/material/consume", ConsumeAsync).RequireAuthorization(WritePolicy);
        return endpoints;
    }

    private static Task<IResult> ConsumeAsync(CommandRequest<ConsumeMaterialPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.Material"),
            (site, actor, input) => new ConsumeMaterialCommand(site, actor, input.Payload.SubmissionId,
                input.OccurredAt, input.Payload.ConsumerSerialNumber, input.Payload.LotId, input.Payload.LotKind,
                input.Payload.MaterialCode, input.Payload.Quantity, input.Payload.UnitOfMeasure,
                input.Payload.SpanFromMeter, input.Payload.SpanToMeter, input.Payload.OperationRunId),
            reason => reason switch
            {
                MaterialReasonCodes.UnitNotFound => 404,
                MaterialReasonCodes.InvalidSpan => 400,
                _ => 409
            }, ct);
}
