using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nvm.Host.Infrastructure;

/// <summary>
/// Ghi ra một health report dạng JSON, nêu tên từng probe, trạng thái của nó và thời gian đã chạy.
/// </summary>
/// <remarks>
/// Writer mặc định chỉ trả lời bằng đúng một từ <c>Unhealthy</c>, không nói gì về việc dependency nào
/// đang gặp sự cố. Nêu đúng tên probe đang lỗi chính là mục đích của endpoint này khi có incident.
/// Nội dung exception chỉ được lộ ra ở Development — ngoài môi trường đó, một probe message có thể
/// để lộ host name hoặc một mảnh connection string cho bất kỳ ai chạm được tới endpoint.
/// </remarks>
internal static class HealthReportWriter
{
    public static Task Write(HttpContext context, HealthReport report)
    {
        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
        var includeErrors = environment.IsDevelopment();

        context.Response.ContentType = "application/json; charset=utf-8";

        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                    description = entry.Value.Description,
                    error = includeErrors ? entry.Value.Exception?.Message : null,
                }),
        });
    }
}
