using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nvm.Hosting;

namespace Nvm.Host.Infrastructure;

/// <summary>
/// Registers the readiness probes for every dependency an App actually needs.
/// </summary>
/// <remarks>
/// <para>
/// Six probes answer <c>/health/ready</c>, and only five of them are registered here. The sixth,
/// <c>bus</c>, is added by <c>AddNvmBus</c> because MassTransit brings its own and the sensible thing
/// is to name and tag that one rather than register a second alongside it. Counting probes by reading
/// this file alone therefore gives five, and the endpoint returns six — the missing one is not
/// missing.
/// </para>
/// <para>
/// <b><c>rabbitmq</c> and <c>bus</c> are not duplicates.</b> <c>rabbitmq</c> asks the broker's
/// management API whether the broker is up and not blocking publishers; <c>bus</c> asks whether this
/// process's own bus started and its receive endpoints are ready. A host that only publishes has no
/// receive endpoints, so <c>bus</c> stays green through a broker outage that <c>rabbitmq</c> catches
/// (measured in M1/C14) — deleting either one leaves a real failure with nothing watching it.
/// </para>
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
                tags: [HealthTags.Live])
            .AddSqlServer(
                sqlServer,
                name: "sqlserver",
                failureStatus: HealthStatus.Unhealthy,
                tags: [HealthTags.Ready],
                timeout: ProbeTimeout)
            .AddNpgSql(
                postgres,
                name: "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: [HealthTags.Ready],
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
                tags: [HealthTags.Ready],
                timeout: ProbeTimeout)
            .AddUrlGroup(
                keycloakDiscovery,
                name: "keycloak",
                failureStatus: HealthStatus.Unhealthy,
                tags: [HealthTags.Ready],
                timeout: ProbeTimeout)
            .AddUrlGroup(
                minioLive,
                name: "minio",
                failureStatus: HealthStatus.Unhealthy,
                tags: [HealthTags.Ready],
                timeout: ProbeTimeout);

        return services;
    }
}
