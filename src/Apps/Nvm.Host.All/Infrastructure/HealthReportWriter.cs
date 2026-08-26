using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nvm.Host.Infrastructure;

/// <summary>
/// Writes a health report as JSON naming every probe, its status and how long it took.
/// </summary>
/// <remarks>
/// The default writer answers with the single word <c>Unhealthy</c>, which says nothing about
/// which dependency is down. Naming the failing probe is the whole point of the endpoint during
/// an incident. Exception text is exposed in Development only — outside it, a probe message could
/// leak a host name or a connection string fragment to whoever can reach the endpoint.
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
