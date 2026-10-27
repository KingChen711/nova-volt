using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Nvm.Observability.Web;

public static class WebObservabilityRegistration
{
    /// <summary>
    /// Như <see cref="ObservabilityRegistration.AddNvmObservability"/>, cộng span/metric cho request HTTP đến.
    /// Probe <c>/health</c> không tạo span: chúng chạy vài giây một lần và chỉ làm nhiễu trace.
    /// </summary>
    public static IHostApplicationBuilder AddNvmWebObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        builder.AddNvmObservability(serviceName);
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(options => options.Filter = context =>
                !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)))
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());
        return builder;
    }
}
