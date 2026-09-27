using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nvm.Kernel.Commands;
using Nvm.Observability;

namespace Nvm.IntegrationTests;

/// <summary>M13: telemetry là best-effort; trace context đi tiếp từ header vào span domain.</summary>
public sealed class ObservabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Lab phá hoại M13: tắt OTel Collector (endpoint không ai nghe) → app vẫn phục vụ và dừng được nhanh.</summary>
    [Fact]
    public async Task CollectorDown_AppKeepsServing_TraceContextFlowsIntoTheDomainSpan_AndShutdownIsPrompt()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Cổng 9 (discard) trên loopback: không có collector nào nghe.
            ["NVM_OTEL:Endpoint"] = "http://127.0.0.1:9",
            ["NVM_OTEL:TimeoutMilliseconds"] = "500",
        });
        builder.AddNvmObservability("nvm-observability-test");
        await using var app = builder.Build();
        app.MapGet("/probe", () =>
        {
            using var domain = KernelTelemetry.Source.StartActivity("command Probe");
            return Results.Ok(new { trace = domain?.TraceId.ToString(), parent = domain?.ParentSpanId.ToString() });
        });
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            // Người gọi (Mendix, thiết bị) đang ở trong trace của nó; HttpClient mang trace đó sang server qua traceparent.
            using var caller = new Activity("caller").SetParentId($"00-{traceId}-00f067aa0ba902b7-01").Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            using var response = await client.SendAsync(request, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            if (i == 0)
            {
                var body = await response.Content.ReadAsStringAsync(Ct);
                body.ShouldContain(traceId);   // span domain nằm trong trace của người gọi, không phải trace mới
            }
        }
        watch.Stop();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));

        var stop = Stopwatch.StartNew();
        await app.StopAsync(Ct);
        stop.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }
}
