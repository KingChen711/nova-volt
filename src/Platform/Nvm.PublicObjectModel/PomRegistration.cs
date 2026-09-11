using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OData.ModelBuilder;
using Npgsql;

namespace Nvm.PublicObjectModel;

public static class PomRegistration
{
    public const string ReadPolicy = "PomRead";

    public static IServiceCollection AddNvmPublicObjectModel(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var authority = configuration["NVM_POM:Authority"]
            ?? $"http://localhost:{configuration["NVM_PORT_KEYCLOAK"] ?? "8081"}/realms/novavolt";
        var connectionString = ReadConnectionString(configuration, environment);
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
            || (!environment.IsDevelopment() && authorityUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("NVM_POM:Authority must be an absolute HTTPS issuer outside Development.");
        }

        services.AddHttpContextAccessor();
        services.AddDbContext<PomReadDbContext>(options =>
            options.UseNpgsql(connectionString).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.MetadataAddress = configuration["NVM_POM:MetadataAddress"]
                ?? authority.TrimEnd('/') + "/.well-known/openid-configuration";
            options.RequireHttpsMetadata = !environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.SaveToken = false;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = authority,
                ValidAudience = configuration["NVM_POM:Audience"] ?? "nvm-api",
                NameClaimType = "sub",
                RoleClaimType = "mendix_roles",
                ClockSkew = TimeSpan.FromSeconds(30),
                // Docker lấy discovery/JWKS qua hostname nội bộ; issuer trên token vẫn là URL login của user.
                IssuerValidator = (issuer, _, _) => string.Equals(issuer, authority, StringComparison.Ordinal)
                    ? issuer : throw new SecurityTokenInvalidIssuerException("Unexpected token issuer."),
            };
        });
        services.AddAuthorizationBuilder().AddPolicy(ReadPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("sub")
            .RequireRole("Operator", "LineLeader")
            .RequireAssertion(context =>
            {
                var sites = context.User.FindAll("site_id").ToArray();
                return sites.Length == 1 && sites[0].Value is "NV1" or "DE1";
            }));

        var model = new ODataConventionModelBuilder { Namespace = "Nvm.Pom.V1", ContainerName = "PublicObjectModel" };
        model.EntitySet<Equipment>("Equipment");
        model.EntitySet<ProductionUnit>("ProductionUnits");
        model.EntitySet<WipBoardRow>("WipBoard");
        services.AddControllers().AddApplicationPart(typeof(EquipmentController).Assembly)
            .AddApplicationPart(typeof(MetadataController).Assembly).AddOData(options => options
            .Select().Filter().OrderBy().Count().SetMaxTop(1000).AddRouteComponents("pom/v1", model.GetEdmModel()));
        return services;
    }

    public static IEndpointRouteBuilder MapNvmPublicObjectModel(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        // Bao gồm metadata/service document do OData cung cấp, không chỉ controller Equipment.
        endpoints.MapControllers().RequireAuthorization(ReadPolicy);
        return endpoints;
    }

    private static string ReadConnectionString(IConfiguration configuration, IHostEnvironment environment)
    {
        if (configuration["NVM_POM:ConnectionString"] is { Length: > 0 } configured)
        {
            return configured;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("NVM_POM:ConnectionString is required outside Development.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = int.Parse(configuration["NVM_PORT_POSTGRES"] ?? "5432", System.Globalization.CultureInfo.InvariantCulture),
            Database = configuration["NVM_POSTGRES_DB"] ?? "novavolt",
            Username = "nvm_pom",
            Password = configuration["NVM_POM_PASSWORD"]
                ?? throw new InvalidOperationException("NVM_POM_PASSWORD is required. Run the POM provisioning step."),
            ApplicationName = "Nvm.PublicObjectModel",
            GssEncryptionMode = GssEncryptionMode.Disable,
            Timeout = 5,
            CommandTimeout = 10,
        }.ConnectionString;
    }
}
