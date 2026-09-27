using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Nvm.IntegrationTests;

/// <summary>
/// App + RabbitMQ qua Toxiproxy như lab chaos broker, cộng OTel Collector và Prometheus chạy đúng file cấu hình và luật
/// cảnh báo trong <c>deploy/</c>. Nhờ vậy lab kiểm chính luật sẽ chạy ở runtime, không phải một bản chép lại.
/// </summary>
public sealed class ObservedBrokerFixture : ToxiproxyRabbitFixture
{
    private IContainer _collector = null!;
    private IContainer _prometheus = null!;
    private IContainer _alertmanager = null!;
    public HttpClient Alertmanager { get; private set; } = null!;
    public HttpClient Prometheus { get; private set; } = null!;

    private static string RepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NovaVolt.Mes.slnx")))
        { directory = directory.Parent; }
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), relative);
    }

    protected override async Task BeforeInfrastructureAsync()
    {
        await base.BeforeInfrastructureAsync();
        _collector = new ContainerBuilder("otel/opentelemetry-collector-contrib:0.140.0")
            .WithNetwork(Network).WithNetworkAliases("otel-collector")
            .WithResourceMapping(new FileInfo(RepoFile("deploy/otel/collector.yaml")), "/etc/otelcol/")
            .WithCommand("--config=/etc/otelcol/collector.yaml")
            .WithPortBinding(4317, true).WithPortBinding(13133, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(13133).ForPath("/")))
            .Build();
        _prometheus = new ContainerBuilder("prom/prometheus:v3.8.0")
            .WithNetwork(Network)
            .WithResourceMapping(new FileInfo(RepoFile("deploy/prometheus/prometheus.yml")), "/etc/prometheus/")
            .WithResourceMapping(new FileInfo(RepoFile("deploy/prometheus/rules/novavolt-slo.yml")), "/etc/prometheus/rules/")
            .WithCommand("--config.file=/etc/prometheus/prometheus.yml")
            .WithPortBinding(9090, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/-/ready")))
            .Build();
        _alertmanager = new ContainerBuilder("prom/alertmanager:v0.34.1")
            .WithNetwork(Network).WithNetworkAliases("alertmanager")
            .WithResourceMapping(new FileInfo(RepoFile("deploy/alertmanager/alertmanager.yml")), "/etc/alertmanager/")
            .WithCommand("--config.file=/etc/alertmanager/alertmanager.yml")
            .WithPortBinding(9093, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9093).ForPath("/-/ready")))
            .Build();
        await Task.WhenAll(_collector.StartAsync(CancellationToken.None), _prometheus.StartAsync(CancellationToken.None),
            _alertmanager.StartAsync(CancellationToken.None));
        Alertmanager = new HttpClient { BaseAddress = new Uri($"http://{_alertmanager.Hostname}:{_alertmanager.GetMappedPublicPort(9093)}/") };
        Prometheus = new HttpClient { BaseAddress = new Uri($"http://{_prometheus.Hostname}:{_prometheus.GetMappedPublicPort(9090)}/") };
    }

    protected override void ConfigureApp(IDictionary<string, string?> environment)
    {
        environment["NVM_OTEL__Endpoint"] = $"http://{_collector.Hostname}:{_collector.GetMappedPublicPort(4317)}";
        environment["OTEL_METRIC_EXPORT_INTERVAL"] = "5000";
    }

    /// <summary>Trạng thái hiện tại của một cảnh báo trong Prometheus: inactive, pending hoặc firing.</summary>
    public async Task<string> AlertStateAsync(string name)
    {
        using var json = JsonDocument.Parse(await Prometheus.GetStringAsync("api/v1/alerts"));
        return json.RootElement.GetProperty("data").GetProperty("alerts").EnumerateArray()
            .Where(a => a.GetProperty("labels").GetProperty("alertname").GetString() == name)
            .Select(a => a.GetProperty("state").GetString()!)
            .OrderBy(state => state == "firing" ? 0 : 1).FirstOrDefault() ?? "inactive";
    }

    /// <summary>Receiver mà Alertmanager đã định tuyến cho cảnh báo đang active, hoặc null nếu cảnh báo chưa tới.</summary>
    public async Task<string?> RoutedReceiverAsync(string name)
    {
        using var json = JsonDocument.Parse(await Alertmanager.GetStringAsync("api/v2/alerts?active=true"));
        return json.RootElement.EnumerateArray()
            .Where(a => a.GetProperty("labels").GetProperty("alertname").GetString() == name)
            .SelectMany(a => a.GetProperty("receivers").EnumerateArray().Select(r => r.GetProperty("name").GetString()))
            .FirstOrDefault();
    }

    public async Task<double?> QueryAsync(string promql)
    {
        using var json = JsonDocument.Parse(await Prometheus.GetStringAsync("api/v1/query?query=" + Uri.EscapeDataString(promql)));
        var result = json.RootElement.GetProperty("data").GetProperty("result");
        return result.GetArrayLength() == 0 ? null
            : double.Parse(result[0].GetProperty("value")[1].GetString()!, CultureInfo.InvariantCulture);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Prometheus?.Dispose();
        Alertmanager?.Dispose();
        if (_alertmanager is not null)
        { await _alertmanager.DisposeAsync(); }
        if (_prometheus is not null)
        { await _prometheus.DisposeAsync(); }
        if (_collector is not null)
        { await _collector.DisposeAsync(); }
    }
}

/// <summary>
/// M13: RabbitMQ chậm thêm 500 ms mỗi chiều thì cảnh báo <c>BrokerPublishSlow</c> (deploy/prometheus/rules) phải bắn; lúc
/// broker bình thường thì không. Command vẫn được nhận trong suốt thời gian đó (lab BrokerLatencyChaos đã đo).
/// </summary>
public sealed class BrokerLatencyAlertLabTests(ObservedBrokerFixture fixture, ITestOutputHelper output)
    : IClassFixture<ObservedBrokerFixture>
{
    private const string Alert = "BrokerPublishSlow";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BrokerPlusFiveHundredMilliseconds_FiresBrokerPublishSlow_AndNormalTrafficDoesNot()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M13 broker latency alert lab (takes about five minutes).");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var traffic = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var _ = await fixture.SendAsync(ExecutionCommandHttpFixture.Request(), fixture.Token());
                }
                catch (HttpRequestException)
                { }
                await Task.Delay(500, CancellationToken.None);
            }
        }, CancellationToken.None);
        try
        {
            // Nền: 90 s traffic bình thường, đủ để luật (rate 1m, for 1m) có dữ liệu và không bắn.
            await Task.Delay(TimeSpan.FromSeconds(90), Ct);
            var baselineP95 = await fixture.QueryAsync("nvm:outbox_publish_duration_seconds:p95_1m");
            (await fixture.AlertStateAsync(Alert)).ShouldBe("inactive");

            await fixture.AddLatencyAsync(500);
            var slow = Stopwatch.StartNew();
            string state;
            while ((state = await fixture.AlertStateAsync(Alert)) != "firing" && slow.Elapsed < TimeSpan.FromMinutes(6))
            { await Task.Delay(TimeSpan.FromSeconds(5), Ct); }
            var slowP95 = await fixture.QueryAsync("nvm:outbox_publish_duration_seconds:p95_1m");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"ALERT_BROKER baseline_p95_s={baselineP95:F3} slow_p95_s={slowP95:F3} state={state} fired_after_s={slow.Elapsed.TotalSeconds:F0}"));
            state.ShouldBe("firing", "BrokerPublishSlow phải bắn khi broker chậm 500 ms mỗi chiều");
            // Prometheus gửi cảnh báo sang Alertmanager (deploy/alertmanager), được định tuyến tới receiver mặc định.
            string? receiver = null;
            var routed = Stopwatch.StartNew();
            while ((receiver = await fixture.RoutedReceiverAsync(Alert)) is null && routed.Elapsed < TimeSpan.FromSeconds(60))
            { await Task.Delay(TimeSpan.FromSeconds(2), Ct); }
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"ALERT_ROUTED receiver={receiver} after_s={routed.Elapsed.TotalSeconds:F0}"));
            receiver.ShouldBe("novavolt-default");
        }
        finally
        {
            await fixture.RemoveLatencyAsync();
            await stop.CancelAsync();
            await traffic;
        }
    }
}
