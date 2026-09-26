using System.Collections.Immutable;
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

public sealed record ReceiveLotPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string LotId,
    [property: JsonRequired] string MaterialCode, [property: JsonRequired] decimal Quantity,
    [property: JsonRequired] string UnitOfMeasure, DateTimeOffset? ExpiresAt, int? MaxExposureMinutes, string? SupplierLotId);
public sealed record LotPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string LotId);
public sealed record OverridePayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string LotId,
    [property: JsonRequired] string Rule, [property: JsonRequired] string Justification,
    [property: JsonRequired] DateTimeOffset ValidUntil, [property: JsonRequired] ImmutableArray<string> SignatureIds);

public static class MaterialRegistration
{
    public const string WritePolicy = "MaterialWrite";
    public const string ReleasePolicy = "MaterialRelease";
    public const string OverridePolicy = "MaterialOverride";

    /// <summary>Đăng ký sau AddNvmCommandStore và event store; dùng chung session của command.</summary>
    public static IServiceCollection AddNvmMaterial(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ICommandHandler<ConsumeMaterialCommand, DomainCommandResult>, ConsumeMaterialHandler>();
        services.TryAddScoped<ICommandValidator<ConsumeMaterialCommand>, ConsumeMaterialValidator>();
        services.TryAddScoped<IMaterialLotStore, SqlMaterialLotStore>();
        services.AddSiteWritePolicy(WritePolicy, "Operator", "LineLeader");
        services.AddSiteWritePolicy(ReleasePolicy, "QaEngineer", "QaManager");
        services.AddSiteWritePolicy(OverridePolicy, "LineLeader", "QaEngineer", "ProductionManager");
        return services;
    }

    public static IEndpointRouteBuilder MapNvmMaterial(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/material/consume", ConsumeAsync).RequireAuthorization(WritePolicy);
        endpoints.MapPost("/api/v1/commands/material/receive-lot", (CommandRequest<ReceiveLotPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ReceiveMaterialLotCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.LotId, input.Payload.MaterialCode,
                input.Payload.Quantity, input.Payload.UnitOfMeasure, input.Payload.ExpiresAt, input.Payload.MaxExposureMinutes,
                input.Payload.SupplierLotId), ct)).RequireAuthorization(WritePolicy);
        endpoints.MapPost("/api/v1/commands/material/release-lot", (CommandRequest<LotPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ReleaseMaterialLotCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.LotId), ct)).RequireAuthorization(ReleasePolicy);
        endpoints.MapPost("/api/v1/commands/material/open-lot", (CommandRequest<LotPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new OpenMaterialLotCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.LotId), ct)).RequireAuthorization(WritePolicy);
        endpoints.MapPost("/api/v1/commands/material/grant-override", (CommandRequest<OverridePayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new GrantMaterialOverrideCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.LotId, input.Payload.Rule, input.Payload.Justification,
                input.Payload.ValidUntil, input.Payload.SignatureIds), ct)).RequireAuthorization(OverridePolicy);
        return endpoints;
    }

    private static Task<IResult> Execute<TPayload>(CommandRequest<TPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs, Func<string, string, CommandRequest<TPayload>, DurableCommand> create,
        CancellationToken ct) where TPayload : class =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.Material"), create,
            reason => reason == MaterialLotProcessor.LotNotFound ? 404 : 409, ct);

    private static Task<IResult> ConsumeAsync(CommandRequest<ConsumeMaterialPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.Material"),
            (site, actor, input) => new ConsumeMaterialCommand(site, actor, input.Payload.SubmissionId,
                input.OccurredAt, input.Payload.ConsumerSerialNumber, input.Payload.LotId, input.Payload.LotKind,
                input.Payload.MaterialCode, input.Payload.Quantity, input.Payload.UnitOfMeasure,
                input.Payload.SpanFromMeter, input.Payload.SpanToMeter, input.Payload.OperationRunId),
            reason => reason switch
            {
                MaterialReasonCodes.UnitNotFound or MaterialLotProcessor.LotNotFound => 404,
                MaterialReasonCodes.InvalidSpan => 400,
                _ => 409
            }, ct);
}
