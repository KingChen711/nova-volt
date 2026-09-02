using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nvm.Ingestion;

namespace Nvm.IntegrationTests;

/// <summary>
/// Khóa chặt nửa phần của C10 contract sống ở phía server. Rate limiter, backoff và xử lý
/// Retry-After của gateway đều vô nghĩa nếu câu trả lời của ingestion khi bị saturation là một
/// connection-pool timeout thay vì một status mà caller đọc được.
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
        // Một ingestion đang bận mà fail readiness probe sẽ bị Docker restart, và một restart làm
        // mất mọi batch đang in-flight. Backpressure không được phép biến thành một outage.
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

    /// <summary>Một endpoint giữ permit của nó cho tới khi test trả lại.</summary>
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
            // Không bao giờ để pipeline giữ một permit: nếu không, một assertion fail sẽ làm treo
            // cả test run thay vì chỉ báo một test đỏ.
            _release.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
