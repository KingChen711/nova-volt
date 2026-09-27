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
using Nvm.Passport.Commands;
using Nvm.Passport.Entities;
using Nvm.Passport.Handlers;

namespace Nvm.Passport.Hosting;

public sealed record BatteryModelPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string Gtin,
    [property: JsonRequired] string ProductCode, [property: JsonRequired] string Manufacturer, [property: JsonRequired] string Chemistry,
    [property: JsonRequired] decimal NominalEnergyKwh, [property: JsonRequired] int ExpectedLifetimeCycles,
    [property: JsonRequired] bool RequiresPassport, [property: JsonRequired] string MaterialCompositionJson,
    [property: JsonRequired] string DismantlingUri, [property: JsonRequired] string SafetyUri);
public sealed record CarbonPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string ProductCode,
    [property: JsonRequired] int Year, [property: JsonRequired] decimal KgCo2ePerKwh,
    [property: JsonRequired] ImmutableDictionary<string, decimal> RecycledContentPercent, [property: JsonRequired] string VerifiedBy);
public sealed record PreparePassportPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string SerialNumber);
public sealed record PublishPassportPayload([property: JsonRequired] string SubmissionId, [property: JsonRequired] string SerialNumber,
    [property: JsonRequired] int Version, [property: JsonRequired] ImmutableArray<string> SignatureIds);

public static class PassportRegistration
{
    public const string ManagePolicy = "PassportManage";
    public const string ReadPolicy = "PassportInternalRead";

    /// <summary>Claim đặt bởi client scope Keycloak của bên đọc: public, consumer, recycler, repairer, regulator.</summary>
    public const string AudienceClaim = "dpp_audience";

    /// <summary>Serial các pin người dùng sở hữu (consumer); mở nhóm Health của đúng pin đó.</summary>
    public const string OwnerClaim = "dpp_owner_of";

    public static IServiceCollection AddNvmPassport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IPassportStore, SqlPassportStore>();
        services.TryAddScoped<IPackEvidenceSource, TracePackEvidenceSource>();
        services.TryAddSingleton<PassportReader>();
        services.AddSiteWritePolicy(ManagePolicy, "ComplianceOwner", "Admin");
        services.AddSiteWritePolicy(ReadPolicy, "ComplianceOwner", "QaManager", "ProductionManager", "Admin");
        return services;
    }

    public static IEndpointRouteBuilder MapNvmPassport(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/commands/passport/define-model", (CommandRequest<BatteryModelPayload>? request,
            HttpContext context, ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new DefineBatteryModelCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.Gtin, input.Payload.ProductCode, input.Payload.Manufacturer,
                input.Payload.Chemistry, input.Payload.NominalEnergyKwh, input.Payload.ExpectedLifetimeCycles,
                input.Payload.RequiresPassport, input.Payload.MaterialCompositionJson, input.Payload.DismantlingUri,
                input.Payload.SafetyUri), ct)).RequireAuthorization(ManagePolicy);
        endpoints.MapPost("/api/v1/commands/passport/record-carbon", (CommandRequest<CarbonPayload>? request, HttpContext context,
            ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new RecordCarbonFootprintCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.ProductCode, input.Payload.Year, input.Payload.KgCo2ePerKwh,
                input.Payload.RecycledContentPercent, input.Payload.VerifiedBy), ct)).RequireAuthorization(ManagePolicy);
        endpoints.MapPost("/api/v1/commands/passport/prepare", (CommandRequest<PreparePassportPayload>? request, HttpContext context,
            ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new PreparePassportCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.SerialNumber), ct)).RequireAuthorization(ManagePolicy);
        endpoints.MapPost("/api/v1/commands/passport/publish", (CommandRequest<PublishPassportPayload>? request, HttpContext context,
            ICommandDispatcher dispatcher, ILoggerFactory logs, CancellationToken ct) =>
            Execute(request, context, dispatcher, logs, (site, actor, input) => new PublishPassportCommand(site, actor,
                input.Payload.SubmissionId, input.OccurredAt, input.Payload.SerialNumber, input.Payload.Version,
                input.Payload.SignatureIds), ct)).RequireAuthorization(ManagePolicy);
        endpoints.MapGet("/api/v1/passport/carbon/{productCode}/{year:int}", async (string productCode, int year, HttpContext context,
            PassportReader reader, CancellationToken ct) => productCode.Length > 100 ? Results.BadRequest()
                : await reader.CarbonAsync(context.User.FindFirst("site_id")!.Value, productCode, year, ct) is { } carbon
                    ? Results.Ok(carbon) : Results.NotFound()).RequireAuthorization(ReadPolicy);

        // GS1 Digital Link: /01/{GTIN-14}/21/{serial}. Công khai; token (nếu có) chỉ đổi tập trường được xem.
        endpoints.MapGet("/01/{gtin}/21/{serial}", async (string gtin, string serial, int? version, HttpContext context,
            PassportReader reader, CancellationToken ct) =>
        {
            if (!Gtin14.IsValid(gtin))
            { return Results.BadRequest(); }
            var user = context.User;
            var authenticated = user.Identity?.IsAuthenticated == true;
            var audiences = authenticated ? user.FindAll(AudienceClaim).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToArray() : [];
            // Nhiều audience trong một token là mơ hồ: không đoán quyền cao hơn, từ chối.
            if (audiences.Length > 1)
            { return Results.Forbid(); }
            var audience = audiences.Length == 1 ? audiences[0] : Audiences.Public;
            if (!Audiences.All.Contains(audience))
            { return Results.Forbid(); }
            var owner = user.FindAll(OwnerClaim).Any(c => string.Equals(c.Value, serial, StringComparison.Ordinal));
            var subject = user.FindFirst("sub")?.Value ?? "anonymous";
            var result = await reader.ReadAsync(gtin, serial, version, audience, owner, subject, ct);
            return result.View is { } view ? Results.Ok(view) : Results.StatusCode(result.StatusCode);
        }).AllowAnonymous();
        return endpoints;
    }

    private static Task<IResult> Execute<TPayload>(CommandRequest<TPayload>? request, HttpContext context,
        ICommandDispatcher dispatcher, ILoggerFactory logs, Func<string, string, CommandRequest<TPayload>, DurableCommand> create,
        CancellationToken ct) where TPayload : class =>
        DurableCommandHttp.ExecuteAsync(request, context, dispatcher, logs.CreateLogger("Nvm.Passport"), create,
            reason => reason switch
            {
                PassportReasonCodes.UnitNotFound or PassportReasonCodes.ModelNotFound or PassportReasonCodes.DraftNotFound => 404,
                "SEPARATION_OF_DUTIES" => 403,
                _ => 409
            }, ct);
}

/// <summary>Migration tường minh của Passport.</summary>
public static class PassportSchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(PassportSchemaMigrator).Assembly, "Nvm.Passport.Hosting.Migrations.",
            connectionString, cancellationToken);
}
