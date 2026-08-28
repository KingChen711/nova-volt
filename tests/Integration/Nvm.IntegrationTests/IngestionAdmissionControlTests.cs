using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nvm.Ingestion;

namespace Nvm.IntegrationTests;

/// <summary>
/// Locks the half of the C10 contract that lives on the server. The gateway's rate limiter,
/// backoff and Retry-After handling are all worthless if ingestion's answer to saturation is a
/// connection-pool timeout instead of a status the caller can read.
/// </summary>
public sealed class IngestionAdmissionControlTests
{
    private const string BusyPath = "/busy";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task SaturatedIngestion_Answers429WithARetryAfterTheGatewayCanHonour()
    {
        await using var busy = new BusyEndpoint();
        using var host = await StartAsync(
            new IngestionOptions
            {
                ConnectionString = "Host=unused",
                MaxConcurrentBatches = 1,
                MaxQueuedBatches = 0,
                RetryAfter = TimeSpan.FromSeconds(3),
            },
            busy);

        using var client = host.GetTestClient();
        var held = client.GetAsync(BusyPath, TestContext.Current.CancellationToken);
        await busy.WaitUntilOccupiedAsync();

        using var refused = await client
            .GetAsync(BusyPath, TestContext.Current.CancellationToken)
            .WaitAsync(Patience, TestContext.Current.CancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.ShouldNotBeNull();
        refused.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(3));

        busy.Release();
        (await held.WaitAsync(Patience, TestContext.Current.CancellationToken)).Dispose();
    }

    [Fact]
    public async Task HealthEndpoints_StayOutsideTheLimiterWhileIngestionIsSaturated()
    {
        // A busy ingestion that fails its readiness probe gets restarted by Docker, and a restart
        // drops every in-flight batch. Backpressure must not be able to become an outage.
        await using var busy = new BusyEndpoint();
        using var host = await StartAsync(
            new IngestionOptions
            {
                ConnectionString = "Host=unused",
                MaxConcurrentBatches = 1,
                MaxQueuedBatches = 0,
            },
            busy);

        using var client = host.GetTestClient();
        var held = client.GetAsync(BusyPath, TestContext.Current.CancellationToken);
        await busy.WaitUntilOccupiedAsync();

        using var probe = await client
            .GetAsync("/health/live", TestContext.Current.CancellationToken)
            .WaitAsync(Patience, TestContext.Current.CancellationToken);

        probe.StatusCode.ShouldBe(HttpStatusCode.OK);

        busy.Release();
        (await held.WaitAsync(Patience, TestContext.Current.CancellationToken)).Dispose();
    }

    private static async Task<IHost> StartAsync(IngestionOptions options, BusyEndpoint busy)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddIngestionAdmissionControl(options);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(endpoints =>
                {
                    endpoints
                        .MapGet(BusyPath, busy.HandleAsync)
                        .RequireRateLimiting(IngestionAdmissionControl.PolicyName);
                    endpoints.MapGet("/health/live", () => "ok");
                });
            });
        });

        return await builder.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An endpoint that holds its permit until the test hands it back.</summary>
    private sealed class BusyEndpoint : IAsyncDisposable
    {
        private readonly TaskCompletionSource _occupied =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task<string> HandleAsync()
        {
            _occupied.TrySetResult();
            await _release.Task;
            return "done";
        }

        internal Task WaitUntilOccupiedAsync() => _occupied.Task.WaitAsync(Patience);

        internal void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync()
        {
            // Never leave the pipeline holding a permit: a failed assertion would otherwise hang
            // the whole test run instead of reporting one red test.
            _release.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
