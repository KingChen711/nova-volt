using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Nvm.IntegrationTests;

/// <summary>App thật nói chuyện với RabbitMQ qua Toxiproxy, để lab chaos thêm độ trễ mạng.</summary>
public sealed class ToxiproxyRabbitFixture : ExecutionCommandHttpFixture
{
    private readonly INetwork _network = new NetworkBuilder().Build();
    private IContainer _proxy = null!;
    private HttpClient _toxiproxy = null!;

    protected override async Task BeforeInfrastructureAsync()
    {
        await _network.CreateAsync(CancellationToken.None);
        _proxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithNetwork(_network)
            .WithPortBinding(8474, true).WithPortBinding(8666, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8474).ForPath("/version")))
            .Build();
        await _proxy.StartAsync(CancellationToken.None);
        _toxiproxy = new HttpClient { BaseAddress = new Uri($"http://{_proxy.Hostname}:{_proxy.GetMappedPublicPort(8474)}/") };
    }

    protected override ContainerBuilder ConfigureRabbit(ContainerBuilder builder) =>
        builder.WithNetwork(_network).WithNetworkAliases("rabbit");

    protected override async Task<(string Host, int Port)> AmqpEndpointAsync(IContainer rabbit)
    {
        using var response = await _toxiproxy.PostAsJsonAsync("proxies",
            new { name = "amqp", listen = "0.0.0.0:8666", upstream = "rabbit:5672", enabled = true });
        if (response.StatusCode != HttpStatusCode.Conflict)
        { response.EnsureSuccessStatusCode(); }
        return (_proxy.Hostname, _proxy.GetMappedPublicPort(8666));
    }

    /// <summary>Thêm độ trễ cả hai chiều trên đường AMQP.</summary>
    public async Task AddLatencyAsync(int milliseconds)
    {
        foreach (var stream in new[] { "upstream", "downstream" })
        {
            using var response = await _toxiproxy.PostAsJsonAsync("proxies/amqp/toxics", new
            {
                name = "latency-" + stream,
                type = "latency",
                stream,
                attributes = new { latency = milliseconds, jitter = 0 },
            });
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task RemoveLatencyAsync()
    {
        foreach (var stream in new[] { "upstream", "downstream" })
        { using var _ = await _toxiproxy.DeleteAsync("proxies/amqp/toxics/latency-" + stream); }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _toxiproxy?.Dispose();
        if (_proxy is not null)
        { await _proxy.DisposeAsync(); }
        await _network.DisposeAsync();
    }
}

/// <summary>
/// M13 chaos: RabbitMQ chậm thêm 500 ms mỗi chiều. Hệ thống chậm nhưng không sập: command vẫn được nhận (outbox tách người
/// gọi khỏi broker), mọi event vẫn tới, app vẫn sống. Phần "alert bắn đúng" cần bộ cảnh báo (Prometheus) chưa có trong
/// compose; lab này chỉ đo phần chịu đựng.
/// </summary>
public sealed class BrokerLatencyChaosLabTests(ToxiproxyRabbitFixture fixture, ITestOutputHelper output)
    : IClassFixture<ToxiproxyRabbitFixture>
{
    private const int Submissions = 20;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BrokerPlusFiveHundredMilliseconds_CommandsStillAccepted_AndEveryEventArrives()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M13 broker latency lab.");
        var baseline = await RoundAsync("baseline");
        await fixture.AddLatencyAsync(500);
        try
        {
            var slow = await RoundAsync("latency500");
            slow.Accept95.ShouldBeLessThan(TimeSpan.FromSeconds(2), "command path does not wait for the broker");
            slow.Delivered.ShouldBeGreaterThan(baseline.Delivered, "broker latency must show up in delivery, not in acceptance");
            using var live = await fixture.Client.GetAsync("/health/live", Ct);
            live.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        { await fixture.RemoveLatencyAsync(); }
    }

    private async Task<(TimeSpan Accept95, TimeSpan Delivered)> RoundAsync(string label)
    {
        var requests = Enumerable.Range(0, Submissions).Select(_ => ExecutionCommandHttpFixture.Request()).ToArray();
        var accept = new List<TimeSpan>();
        var clock = Stopwatch.StartNew();
        foreach (var request in requests)
        {
            var one = Stopwatch.StartNew();
            using var response = await fixture.SendAsync(request, fixture.Token());
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            accept.Add(one.Elapsed);
        }
        var wanted = requests.Select(r => r.IdempotencyKey.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!wanted.IsSubsetOf(seen) && clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            foreach (var row in await fixture.DrainEventsAsync())
            { seen.Add(row.GetProperty("properties").GetProperty("headers").GetProperty("ce_id").GetString()!); }
            await Task.Delay(100, Ct);
        }
        var delivered = clock.Elapsed;
        accept.Sort();
        var p95 = accept[(int)Math.Ceiling(accept.Count * 0.95) - 1];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CHAOS_BROKER {label} accept_p95_ms={p95.TotalMilliseconds:F0} all_delivered_s={delivered.TotalSeconds:F1} delivered={wanted.Count(seen.Contains)}/{wanted.Count}"));
        wanted.Except(seen).ShouldBeEmpty("không event nào được mất khi broker chậm");
        return (p95, delivered);
    }
}
