using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nvm.Host.Infrastructure;

/// <summary>
/// Registers the readiness probes for every dependency an App actually needs.
/// </summary>
/// <remarks>
/// <para>
/// EMQX is deliberately absent. The MQTT broker sits on <c>ot-net</c> and <c>dmz-net</c> only;
/// nothing on the IT tier talks to it, and <c>Nvm.EdgeGateway</c> — the service that does — arrives
/// in M2. A readiness probe for a dependency the process does not use would take the app out of
/// rotation for an outage that cannot affect it.
/// </para>
/// <para>
/// Every probe is lazy: it opens its connection when the endpoint is called, never at startup.
/// Constructing a connection during registration would stop the host from starting whenever a
/// database is briefly unavailable, which is the opposite of what N15 requires.
/// </para>
/// </remarks>
internal static class HealthCheckRegistration
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static IServiceCollection AddDependencyHealthChecks(this IServiceCollection services)
    {
        var sqlServer = string.Create(
            CultureInfo.InvariantCulture,
            $"Server=localhost,{DotEnvLoader.Required("NVM_PORT_MSSQL")};Database=NovaVolt;" +
            $"User Id=nvm_app;Password={DotEnvLoader.Required("NVM_MSSQL_APP_PASSWORD")};" +
            $"TrustServerCertificate=True;Encrypt=True;Connect Timeout=3");

        var postgres = string.Create(
            CultureInfo.InvariantCulture,
            $"Host=localhost;Port={DotEnvLoader.Required("NVM_PORT_POSTGRES")};" +
            $"Database={DotEnvLoader.Required("NVM_POSTGRES_DB")};" +
            $"Username={DotEnvLoader.Required("NVM_POSTGRES_USER")};" +
            $"Password={DotEnvLoader.Required("NVM_POSTGRES_PASSWORD")};Timeout=3");

        var keycloakDiscovery = new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"http://localhost:{DotEnvLoader.Required("NVM_PORT_KEYCLOAK")}/realms/novavolt/.well-known/openid-configuration"));

        var minioLive = new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"http://localhost:{DotEnvLoader.Required("NVM_PORT_MINIO")}/minio/health/live"));

        var rabbitManagement = new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"http://localhost:{DotEnvLoader.Required("NVM_PORT_RABBITMQ_UI")}/api/health/checks/alarms"));

        var rabbitCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{DotEnvLoader.Required("NVM_RABBITMQ_USER")}:{DotEnvLoader.Required("NVM_RABBITMQ_PASSWORD")}"));

        services
            .AddHealthChecks()
            .AddCheck(
                "self",
                () => HealthCheckResult.Healthy("Process is running."),
                tags: ["live"])
            .AddSqlServer(
                sqlServer,
                name: "sqlserver",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: ProbeTimeout)
            .AddNpgSql(
                postgres,
                name: "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: ProbeTimeout)
            .AddUrlGroup(
                // The alarms endpoint, not the overview one: a broker that has blocked publishers
                // on a memory or disk alarm still answers /api/overview with 200.
                uri: rabbitManagement,
                configureClient: (_, client) =>
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Basic", rabbitCredentials),
                name: "rabbitmq",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: ProbeTimeout)
            .AddUrlGroup(
                keycloakDiscovery,
                name: "keycloak",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: ProbeTimeout)
            .AddUrlGroup(
                minioLive,
                name: "minio",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: ProbeTimeout);

        return services;
    }
}
