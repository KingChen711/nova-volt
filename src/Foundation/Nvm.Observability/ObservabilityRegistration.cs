using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Nvm.Observability;

public static class ObservabilityRegistration
{
    /// <summary>Mọi ActivitySource/Meter của NovaVolt bắt đầu bằng tiền tố này.</summary>
    public const string SourcePrefix = "NovaVolt.";

    /// <summary>
    /// Bật trace, metric và log OpenTelemetry cho một host. Có <c>NVM_OTEL:Endpoint</c> thì xuất OTLP; không có thì
    /// vẫn thu (để test và listener nội bộ dùng) nhưng không gửi đi đâu. Exporter lỗi hoặc collector tắt không làm
    /// request nào hỏng: OTLP exporter chạy theo lô ở nền và bỏ dữ liệu khi không gửi được (lab M13).
    /// </summary>
    public static IHostApplicationBuilder AddNvmObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var endpoint = builder.Configuration["NVM_OTEL:Endpoint"];
        var exportTimeout = int.TryParse(builder.Configuration["NVM_OTEL:TimeoutMilliseconds"], out var timeout) ? timeout : 2000;
        void Otlp(OtlpExporterOptions options)
        {
            options.Endpoint = new Uri(endpoint!);
            options.Protocol = OtlpExportProtocol.Grpc;
            // Không để một collector treo giữ shutdown hay lô export lâu hơn vài giây.
            options.TimeoutMilliseconds = exportTimeout;
        }

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceNamespace: "novavolt")
                .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing.AddSource(SourcePrefix + "*", "MassTransit")
                    .AddHttpClientInstrumentation();
                if (endpoint is not null)
                { tracing.AddOtlpExporter(Otlp); }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(SourcePrefix + "*", "MassTransit")
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (endpoint is not null)
                { metrics.AddOtlpExporter(Otlp); }
            });
        if (endpoint is not null)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.AddOtlpExporter(Otlp);
            });
        }
        _ = telemetry;
        return builder;
    }
}
