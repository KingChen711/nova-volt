using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.EventSourcing;
using Nvm.Traceability.Commands;

namespace Nvm.Traceability.Hosting;

public sealed record TraceabilityRequest<TPayload>(
    string IdempotencyKey, string SiteId,
    [property: JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonRequired] TPayload Payload);

public sealed record SerializeUnitPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] string ProductCode,
    [property: JsonRequired] string WorkOrderId, [property: JsonRequired] string RoutingVersion);
public sealed record StartStepPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] string StepCode,
    [property: JsonRequired] string OperationRunId, [property: JsonRequired] string EquipmentPath);
public sealed record CompleteStepPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] string StepCode,
    [property: JsonRequired] string OperationRunId);
public sealed record RecordUnitMeasurementPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] string StepCode,
    [property: JsonRequired] string OperationRunId, [property: JsonRequired] string EquipmentPath,
    [property: JsonRequired] string SignalCode,
    [property: JsonRequired] decimal Value, [property: JsonRequired] string UnitOfMeasure);

public static class TraceabilityEndpoints
{
    private const string Policy = "TraceabilityWrite";
    private const string IdentityConflictMessage = "Idempotency identity or payload conflict.";

    public static IServiceCollection AddNvmTraceabilityAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .RequireAuthenticatedUser().RequireRole("Operator", "LineLeader")
            .RequireAssertion(context =>
            {
                var sites = context.User.FindAll("site_id").ToArray();
                var actors = context.User.FindAll("sub").ToArray();
                return sites.Length == 1 && sites[0].Value.Length == 3 &&
                    sites[0].Value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)) &&
                    actors.Length == 1 && actors[0].Value.Length is > 0 and <= 200;
            }));
        return services;
    }

    public static IEndpointRouteBuilder MapNvmTraceability(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/commands/traceability/serialize-unit", SerializeAsync)
            .RequireAuthorization(Policy);
        endpoints.MapPost("/api/v1/commands/production/start-step", StartAsync)
            .RequireAuthorization(Policy);
        endpoints.MapPost("/api/v1/commands/production/complete-step", CompleteAsync)
            .RequireAuthorization(Policy);
        endpoints.MapPost("/api/v1/commands/traceability/record-measurement", RecordMeasurementAsync)
            .RequireAuthorization(Policy);
        return endpoints;
    }

    private static Task<IResult> SerializeAsync(TraceabilityRequest<SerializeUnitPayload>? request,
        HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
        ExecuteAsync(request, context, dispatcher, logs,
            (site, actor, input) => new SerializeUnitCommand(site, actor, input.Payload.SubmissionId,
                input.Payload.SerialNumber, input.OccurredAt, input.Payload.ProductCode,
                input.Payload.WorkOrderId, input.Payload.RoutingVersion), ct);

    private static Task<IResult> StartAsync(TraceabilityRequest<StartStepPayload>? request,
        HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
        ExecuteAsync(request, context, dispatcher, logs,
            (site, actor, input) => new StartStepCommand(site, actor, input.Payload.SubmissionId,
                input.Payload.SerialNumber, input.OccurredAt, input.Payload.StepCode,
                input.Payload.OperationRunId, input.Payload.EquipmentPath), ct);

    private static Task<IResult> CompleteAsync(TraceabilityRequest<CompleteStepPayload>? request,
        HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
        ExecuteAsync(request, context, dispatcher, logs,
            (site, actor, input) => new CompleteStepCommand(site, actor, input.Payload.SubmissionId,
                input.Payload.SerialNumber, input.OccurredAt, input.Payload.StepCode,
                input.Payload.OperationRunId), ct);

    private static Task<IResult> RecordMeasurementAsync(TraceabilityRequest<RecordUnitMeasurementPayload>? request,
        HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
        ExecuteAsync(request, context, dispatcher, logs,
            (site, actor, input) => new RecordMeasurementCommand(site, actor, input.Payload.SubmissionId,
                input.Payload.SerialNumber, input.OccurredAt, input.Payload.StepCode,
                input.Payload.OperationRunId, input.Payload.EquipmentPath, input.Payload.SignalCode,
                input.Payload.Value, input.Payload.UnitOfMeasure), ct);

    private static async Task<IResult> ExecuteAsync<TPayload>(TraceabilityRequest<TPayload>? request,
        HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs,
        Func<string, string, TraceabilityRequest<TPayload>, UnitCommand> create,
        CancellationToken ct) where TPayload : class
    {
        if (request?.Payload is null || string.IsNullOrWhiteSpace(request.SiteId) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.OccurredAt == default ||
            !ValidPayload(request.Payload))
        { return Result(400, UnitReasonCodes.InvalidInput); }
        var site = context.User.FindFirst("site_id")?.Value ?? "";
        var actor = context.User.FindFirst("sub")?.Value ?? "";
        if (!string.Equals(site, request.SiteId, StringComparison.Ordinal))
        { return Result(403, UnitReasonCodes.SiteMismatch); }
        UnitCommand command;
        try
        { command = create(site, actor, request); }
        catch (ArgumentException) { return Result(400, UnitReasonCodes.InvalidInput); }
        if (!Guid.TryParse(request.IdempotencyKey, out var supplied) ||
            supplied != command.IdempotencyKey.Value)
        { return Result(400, "KEY_MISMATCH"); }
        try
        {
            var outcome = await dispatcher.DispatchAsync<UnitCommandResult>(command, ct).ConfigureAwait(false);
            return Results.Json(outcome, statusCode: outcome.Accepted ? 202 : RejectionStatus(outcome.ReasonCode));
        }
        catch (CommandValidationException) { return Result(400, UnitReasonCodes.InvalidInput); }
        catch (EventConcurrencyException) { return Result(409, "CONCURRENT_MODIFICATION"); }
        catch (EventIdentityConflictException) { return Result(409, "IDENTITY_CONFLICT"); }
        catch (InvalidOperationException error) when (
            string.Equals(error.Message, IdentityConflictMessage, StringComparison.Ordinal))
        { return Result(409, "IDENTITY_CONFLICT"); }
        catch (ArgumentException) { return Result(409, "IDENTITY_CONFLICT"); }
        catch (SqlException error)
        {
            logs.CreateLogger("Nvm.Traceability").LogError(error,
                "Traceability command store unavailable for site {SiteId}", site);
            return Result(503, "STORE_UNAVAILABLE");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logs.CreateLogger("Nvm.Traceability").LogError(error,
                "Traceability command failed for site {SiteId}", site);
            return Result(500, "INTERNAL_ERROR");
        }
    }

    private static int RejectionStatus(string reason) => reason switch
    {
        UnitReasonCodes.InvalidInput or UnitReasonCodes.InvalidSerial => 400,
        UnitReasonCodes.SiteMismatch or UnitReasonCodes.UnauthorizedTransition => 403,
        UnitReasonCodes.UnitNotFound or UnitReasonCodes.RoutingNotFound => 404,
        _ => 409
    };

    private static IResult Result(int status, string reason) =>
        Results.Json(new UnitCommandResult(false, reason, null, null), statusCode: status);

    private static bool ValidPayload<TPayload>(TPayload payload) where TPayload : class => payload switch
    {
        SerializeUnitPayload item => Has(item.SubmissionId, item.SerialNumber, item.ProductCode,
            item.WorkOrderId, item.RoutingVersion),
        StartStepPayload item => Has(item.SubmissionId, item.SerialNumber, item.StepCode,
            item.OperationRunId, item.EquipmentPath),
        CompleteStepPayload item => Has(item.SubmissionId, item.SerialNumber, item.StepCode,
            item.OperationRunId),
        RecordUnitMeasurementPayload item => Has(item.SubmissionId, item.SerialNumber,
            item.StepCode, item.OperationRunId, item.EquipmentPath, item.SignalCode, item.UnitOfMeasure),
        _ => false
    };

    private static bool Has(params string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value));
}
