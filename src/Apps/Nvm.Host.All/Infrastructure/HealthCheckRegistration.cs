using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nvm.Hosting;

namespace Nvm.Host.Infrastructure;

/// <summary>
/// Đăng ký readiness probe cho mọi dependency mà một App thực sự cần.
/// </summary>
/// <remarks>
/// <para>
/// Sáu probe trả lời <c>/health/ready</c>, nhưng chỉ năm trong số đó được đăng ký ở đây. Cái thứ
/// sáu, <c>bus</c>, được thêm bởi <c>AddNvmBus</c> vì MassTransit tự mang theo probe của nó, và cách
/// hợp lý là đặt tên và gắn tag cho cái đó thay vì đăng ký thêm một cái nữa bên cạnh. Vì vậy đếm
/// probe chỉ bằng cách đọc file này sẽ ra năm, còn endpoint trả về sáu — cái thiếu không phải bị mất.
/// </para>
/// <para>
/// <b><c>rabbitmq</c> và <c>bus</c> không phải hai bản trùng.</b> <c>rabbitmq</c> hỏi management API
/// của broker xem broker có đang chạy và không chặn publisher hay không; <c>bus</c> hỏi xem bus của
/// chính process này đã khởi động và receive endpoint của nó đã sẵn sàng chưa. Một host chỉ publish
/// thì không có receive endpoint nào, nên <c>bus</c> vẫn xanh xuyên suốt một đợt broker outage mà
/// <c>rabbitmq</c> bắt được (đo ở M1/C14) — xoá bỏ một trong hai để lại một sự cố thật mà không ai
/// theo dõi.
/// </para>
/// <para>
/// EMQX cố ý không có mặt. MQTT broker chỉ nằm trên <c>ot-net</c> và <c>dmz-net</c>; không có gì ở
/// tầng IT nói chuyện với nó, và <c>Nvm.EdgeGateway</c> — service làm việc đó — sẽ đến ở M2. Một
/// readiness probe cho một dependency mà process không dùng sẽ đưa app ra khỏi rotation vì một
/// outage vốn không thể ảnh hưởng tới nó.
/// </para>
/// <para>
/// Mọi probe đều lazy: nó mở connection khi endpoint được gọi, không bao giờ lúc startup. Dựng
/// connection ngay lúc đăng ký sẽ khiến host không khởi động được mỗi khi một database tạm thời
/// không sẵn sàng, ngược hẳn với điều N15 yêu cầu.
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
                // Endpoint alarms, không phải endpoint overview: một broker đã chặn publisher vì
                // alarm bộ nhớ hoặc đĩa vẫn trả lời /api/overview bằng 200.
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
