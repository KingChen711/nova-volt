using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Contracts.Events.Grading;
using Nvm.Grading.Commands;
using Nvm.Grading.Handlers;
using Nvm.Grading.Matching;
using Nvm.PublicObjectModel;

namespace Nvm.Grading.Hosting;

public sealed record DefineRuleSetPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string RuleSetId, [property: JsonRequired] int Version,
    [property: JsonRequired] string ProductCode, [property: JsonRequired] DateTimeOffset EffectiveFrom,
    [property: JsonRequired] ImmutableArray<GradingBin> Bins, ImmutableArray<GradingReject> Rejects);
public sealed record ApproveRuleSetPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string RuleSetId, [property: JsonRequired] int Version);
public sealed record GradeUnitPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber, [property: JsonRequired] decimal CapacityAh,
    [property: JsonRequired] decimal OcvMillivolt, [property: JsonRequired] decimal DcirMilliOhm, decimal? OcvDriftMillivolt);
public sealed record ReevaluateUnitPayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string SerialNumber);

/// <summary>Yêu cầu chạy matching trên kho bin của site; chỉ đề xuất, không lắp ráp gì.</summary>
public sealed record MatchingRunRequest(string ProductCode, string? Algorithm, int? ReservedPerBin, int? MaxModules);

public sealed record MatchingRunResponse(string SiteId, string Algorithm, int Cells, int Modules,
    IReadOnlyList<MatchedModule> ProposedModules, IReadOnlyList<string> Leftover, IReadOnlyList<string> Reserved,
    IReadOnlyList<string> TooOld, double RuntimeSeconds);

public static class GradingRegistration
{
    public const string WritePolicy = "GradingWrite";
    public const string ApprovePolicy = "GradingApprove";

    public static IServiceCollection AddNvmGrading(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IGradingRuleSetStore, SqlGradingRuleSetStore>();
        services.AddSiteWritePolicy(WritePolicy, "Operator", "LineLeader", "QualityEngineer");
        services.AddSiteWritePolicy(ApprovePolicy, "LineLeader", "QualityEngineer");
        return services;
    }

    public static IEndpointRouteBuilder MapNvmGrading(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/grading/define-rule-set", (CommandRequest<DefineRuleSetPayload>? request,
            HttpContext context, Nvm.Kernel.Commands.ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new DefineGradingRuleSetCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.RuleSetId, input.Payload.Version,
                input.Payload.ProductCode, input.Payload.EffectiveFrom, input.Payload.Bins, input.Payload.Rejects), ct))
            .RequireAuthorization(ApprovePolicy);
        endpoints.MapPost("/api/v1/commands/grading/approve-rule-set", (CommandRequest<ApproveRuleSetPayload>? request,
            HttpContext context, Nvm.Kernel.Commands.ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ApproveGradingRuleSetCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.RuleSetId, input.Payload.Version), ct))
            .RequireAuthorization(ApprovePolicy);
        endpoints.MapPost("/api/v1/commands/grading/grade-unit", (CommandRequest<GradeUnitPayload>? request,
            HttpContext context, Nvm.Kernel.Commands.ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new GradeUnitCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.SerialNumber, input.Payload.CapacityAh,
                input.Payload.OcvMillivolt, input.Payload.DcirMilliOhm, input.Payload.OcvDriftMillivolt), ct))
            .RequireAuthorization(WritePolicy);
        endpoints.MapPost("/api/v1/commands/grading/reevaluate-unit", (CommandRequest<ReevaluateUnitPayload>? request,
            HttpContext context, Nvm.Kernel.Commands.ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ReevaluateUnitCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.SerialNumber), ct))
            .RequireAuthorization(ApprovePolicy);

        var matching = endpoints.MapGroup("/api/v1/matching").RequireAuthorization(PomRegistration.ReadPolicy);
        matching.MapGet("/bins/{productCode}", async (string productCode, HttpContext context, BinInventoryQueries inventory,
            CancellationToken ct) => productCode.Length is 0 or > 100 ? Results.BadRequest()
                : Results.Ok(await inventory.CountsAsync(context.User.FindFirst("site_id")!.Value, productCode, ct)));
        matching.MapPost("/run", RunAsync);
        return endpoints;
    }

    /// <summary>Đề xuất module trên kho của site đang đăng nhập; không đọc được kho site khác.</summary>
    internal static async Task<IResult> RunAsync(MatchingRunRequest? request, HttpContext context,
        BinInventoryQueries inventory, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProductCode) || request.ProductCode.Length > 100 ||
            request.ReservedPerBin is < 0 or > 10_000 || request.MaxModules is < 1 or > 100_000 ||
            request.Algorithm is not (null or "cp-sat" or "greedy"))
        { return Results.BadRequest(); }
        var site = context.User.FindFirst("site_id")!.Value;
        var cells = (await inventory.AvailableAsync(site, request.ProductCode, ct))
            .Select(c => new MatchCell(c.SerialNumber, c.BinCode, c.CapacityAh, c.OcvMillivolt, c.DcirMilliOhm, c.LotId,
                c.GradedAt)).ToList();
        IModuleMatcher matcher = request.Algorithm == "greedy" ? new GreedyMatcher() : new CpSatMatcher();
        var result = matcher.Match(cells, new MatchingSpec
        { ReservedPerBin = request.ReservedPerBin ?? 0, MaxModules = request.MaxModules }, clock.GetUtcNow());
        return Results.Ok(new MatchingRunResponse(site, result.Algorithm, cells.Count, result.Modules.Length,
            result.Modules, result.Leftover, result.Reserved, result.TooOld, result.Runtime.TotalSeconds));
    }

    private static Task<IResult> Execute<TPayload>(CommandRequest<TPayload>? request, HttpContext context,
        Nvm.Kernel.Commands.ICommandDispatcher dispatcher, ILoggerFactory logs,
        Func<string, string, CommandRequest<TPayload>, Nvm.Kernel.Commands.DurableCommand> create, CancellationToken ct)
        where TPayload : class =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.Grading"), create,
            reason => reason switch
            {
                GradingReasonCodes.RuleSetNotFound or GradingReasonCodes.UnitNotFound => 404,
                GradingReasonCodes.InvalidRuleSet => 400,
                GradingReasonCodes.SeparationOfDuties => 403,
                _ => 409
            }, ct);
}
