using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Contracts.Ports;
using Nvm.Contracts.Queries;
using Nvm.Kernel.Commands;
using Nvm.Quality.Commands;
using Nvm.Quality.Entities;
using Nvm.Quality.Handlers;
using Nvm.Quality.Ports;

namespace Nvm.Quality.Hosting;

public sealed record PlaceHoldPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string TargetKind,
    [property: JsonRequired] string TargetId, decimal? SpanFromMeter, decimal? SpanToMeter,
    [property: JsonRequired] string ReasonCode, string? NcrId);
public sealed record SignPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string SubjectType,
    [property: JsonRequired] string SubjectId, [property: JsonRequired] string Meaning,
    [property: JsonRequired] string SignerRole, [property: JsonRequired] string ContentSha256, string? Disposition,
    [property: JsonRequired] string Password);
public sealed record ReleaseHoldPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string HoldId,
    [property: JsonRequired] ImmutableArray<string> SignatureIds);
public sealed record DispositionPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string NcrId,
    [property: JsonRequired] string Disposition, [property: JsonRequired] ImmutableArray<string> SignatureIds);
public sealed record SpcSamplePayload([property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string Characteristic, [property: JsonRequired] string SubgroupId,
    [property: JsonRequired] ImmutableArray<decimal> Values);

public static class QualityRegistration
{
    public const string HoldPolicy = "QualityHold";
    public const string SignPolicy = "QualitySign";
    public const string DecidePolicy = "QualityDecide";
    public const string SamplePolicy = "QualitySample";
    public const string ReadPolicy = "QualityRead";
    private static readonly string[] Signers = [ApprovalPolicy.QualityManager, ApprovalPolicy.ProductionManager,
        ApprovalPolicy.QualityEngineer, ApprovalPolicy.CustomerRepresentative];

    /// <summary>Đăng ký sau AddNvmCommandStore và event store; mọi adapter dùng chung session của command.</summary>
    public static IServiceCollection AddNvmQuality(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<SqlUnitQualityFacet>();
        services.TryAddScoped<IUnitQualityFacet>(provider => provider.GetRequiredService<SqlUnitQualityFacet>());
        services.TryAddScoped<IUnitQualityWriter>(provider => provider.GetRequiredService<SqlUnitQualityFacet>());
        services.TryAddScoped<IQualityIncidents, SqlQualityIncidents>();
        services.TryAddScoped<SqlQualityStores>();
        services.TryAddScoped<IHoldStore>(provider => provider.GetRequiredService<SqlQualityStores>());
        services.TryAddScoped<ISignatureStore>(provider => provider.GetRequiredService<SqlQualityStores>());
        services.TryAddScoped<INcrStore>(provider => provider.GetRequiredService<SqlQualityStores>());
        services.TryAddScoped<ISpcStore, SqlSpcStore>();
        services.TryAddScoped<ISignatureVerifier, SqlSignatureVerifier>();
        services.TryAddScoped<IMaterialHoldCheck, SqlMaterialHoldCheck>();
        services.TryAddScoped<IDownstreamUnits, TraceDownstreamUnits>();
        services.TryAddSingleton<SqlQualityQueries>();

        var authority = configuration?["NVM_POM:Authority"]
            ?? $"http://localhost:{configuration?["NVM_PORT_KEYCLOAK"] ?? "8081"}/realms/novavolt";
        services.TryAddSingleton(new ReauthenticationOptions(
            new Uri(configuration?["NVM_ESIGN:TokenEndpoint"] ?? authority.TrimEnd('/') + "/protocol/openid-connect/token"),
            configuration?["NVM_ESIGN:ClientId"] ?? "nvm-esign"));
        services.AddHttpClient<IReauthenticator, KeycloakReauthenticator>();
        var delay = int.TryParse(configuration?["NVM_QUALITY:CascadeDelayMs"], System.Globalization.CultureInfo.InvariantCulture,
            out var ms) ? ms : 0;
        services.TryAddSingleton(new HoldCascadeOptions(TimeSpan.FromMilliseconds(delay),
            !string.Equals(configuration?["NVM_QUALITY:CascadeWorker"], "false", StringComparison.OrdinalIgnoreCase)));
        services.TryAddSingleton<HoldCascadeWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<HoldCascadeWorker>());

        services.AddSiteWritePolicy(HoldPolicy, ApprovalPolicy.QualityEngineer, ApprovalPolicy.QualityManager);
        services.AddSiteWritePolicy(SignPolicy, Signers);
        services.AddSiteWritePolicy(DecidePolicy, ApprovalPolicy.QualityManager, ApprovalPolicy.ProductionManager);
        services.AddSiteWritePolicy(SamplePolicy, "Operator", "LineLeader", ApprovalPolicy.QualityEngineer);
        services.AddSiteWritePolicy(ReadPolicy, [.. Signers, "Operator", "LineLeader"]);
        return services;
    }

    public static IEndpointRouteBuilder MapNvmQuality(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/quality/place-hold", (CommandRequest<PlaceHoldPayload>? request, HttpContext context,
            ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new PlaceHoldCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.TargetKind, input.Payload.TargetId,
                input.Payload.SpanFromMeter, input.Payload.SpanToMeter, input.Payload.ReasonCode, input.Payload.NcrId), ct))
            .RequireAuthorization(HoldPolicy);
        endpoints.MapPost("/api/v1/commands/quality/sign", SignAsync).RequireAuthorization(SignPolicy);
        endpoints.MapPost("/api/v1/commands/quality/release-hold", (CommandRequest<ReleaseHoldPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ReleaseHoldCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.HoldId, input.Payload.SignatureIds), ct))
            .RequireAuthorization(DecidePolicy);
        endpoints.MapPost("/api/v1/commands/quality/apply-disposition", (CommandRequest<DispositionPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new ApplyDispositionCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.NcrId, input.Payload.Disposition,
                input.Payload.SignatureIds), ct))
            .RequireAuthorization(DecidePolicy);
        endpoints.MapPost("/api/v1/commands/quality/record-spc-sample", (CommandRequest<SpcSamplePayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new RecordSpcSampleCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.Characteristic, input.Payload.SubgroupId,
                input.Payload.Values), ct))
            .RequireAuthorization(SamplePolicy);

        var read = endpoints.MapGroup("/api/v1/quality").RequireAuthorization(ReadPolicy);
        read.MapGet("/holds/{holdId}", async (string holdId, HttpContext context, SqlQualityQueries queries,
            CancellationToken ct) => holdId.Length > 64 ? Results.BadRequest()
                : await queries.HoldAsync(Site(context), holdId, ct) is { } hold ? Results.Ok(hold) : Results.NotFound());
        read.MapGet("/signatures/verify", async (HttpContext context, SqlQualityQueries queries, CancellationToken ct) =>
        {
            var chain = await queries.SignatureChainAsync(Site(context), ct);
            var broken = SignatureChain.FirstBroken(chain);
            return Results.Ok(new { Signatures = chain.Count, Valid = broken is null, FirstBrokenIndex = broken });
        });
        read.MapGet("/spc/{characteristic}", async (string characteristic, decimal? lsl, decimal? usl, int? last,
            HttpContext context, SqlQualityQueries queries, CancellationToken ct) =>
        {
            if (characteristic.Length > 64 || last is < 2 or > 500)
            { return Results.BadRequest(); }
            var groups = await queries.SpcAsync(Site(context), characteristic, last ?? 25, ct);
            if (groups.Count < 2)
            { return Results.Ok(new { Subgroups = groups, Chart = (XbarRChart?)null }); }
            try
            { return Results.Ok(new { Subgroups = groups, Chart = XbarR.Compute(groups, lsl, usl) }); }
            catch (ArgumentException)
            { return Results.UnprocessableEntity(); }
        });
        return endpoints;
    }

    /// <summary>Xác thực lại bằng mật khẩu trước khi dispatch; mật khẩu không đi vào command hay outcome.</summary>
    internal static async Task<IResult> SignAsync(CommandRequest<SignPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, IReauthenticator reauthenticator, ILoggerFactory logs, CancellationToken ct)
    {
        if (request?.Payload is not { } payload || string.IsNullOrEmpty(payload.Password))
        { return Results.Json(DomainCommandResult.Reject(DomainCommandResult.InvalidInput), statusCode: 400); }
        if (!context.User.IsInRole(payload.SignerRole))
        {
            return Results.Json(DomainCommandResult.Reject(QualityReasonCodes.RoleNotHeld, "Bạn không giữ vai trò này."),
                statusCode: 403);
        }
        var subject = context.User.FindFirst("sub")?.Value ?? "";
        var username = context.User.FindFirst("preferred_username")?.Value ?? subject;
        if (!await reauthenticator.VerifyAsync(subject, username, payload.Password, ct))
        {
            return Results.Json(DomainCommandResult.Reject(QualityReasonCodes.ReauthenticationFailed,
                "Xác thực lại không thành công."), statusCode: 401);
        }
        return await Execute(request, context, dispatcher, logs, (site, actor, input) => new SignCommand(site, actor,
            input.Payload.SubmissionId, input.OccurredAt, input.Payload.SubjectType, input.Payload.SubjectId,
            input.Payload.Meaning, input.Payload.SignerRole, input.Payload.ContentSha256, input.Payload.Disposition), ct);
    }

    private static string Site(HttpContext context) => context.User.FindFirst("site_id")!.Value;

    private static Task<IResult> Execute<TPayload>(CommandRequest<TPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs, Func<string, string, CommandRequest<TPayload>, DurableCommand> create,
        CancellationToken ct) where TPayload : class =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.Quality"), create,
            reason => reason switch
            {
                QualityReasonCodes.HoldNotFound or QualityReasonCodes.NcrNotFound or QualityReasonCodes.SignatureNotFound => 404,
                QualityReasonCodes.SeparationOfDuties => 403,
                _ => 409
            }, ct);
}
