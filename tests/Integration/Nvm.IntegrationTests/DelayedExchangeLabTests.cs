using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using RabbitMQ.Client;

namespace Nvm.IntegrationTests;

/// <summary>
/// Lab M7 #1 (scope §9/M7, ADR-015): dùng RabbitMQ delayed message plugin cho timeout aging 10 ngày thay vì bảng
/// ProcessTimeouts trong SQL. Đo bộ nhớ broker khi giữ 30.000 timeout, giới hạn độ dài delay, việc xem/huỷ timeout đang
/// chờ, và timeout có sống qua restart broker không.
/// Plugin 4.2.0 chỉ chạy với broker 4.2.x (<c>broker_version_requirements</c>), nên lab dùng rabbitmq:4.2.9, không phải
/// bản 4.3.5 của runtime. File plugin không nằm trong git: đặt ở NVM_DELAYED_PLUGIN hoặc %LOCALAPPDATA%/nvm-labs.
/// </summary>
public sealed class DelayedExchangeLabTests(ITestOutputHelper output)
{
    private const int Timeouts = 30_000;
    private const string Exchange = "lab.formation-timeouts";
    private const string Queue = "lab.formation-timeouts.due";
    private static readonly long TenDaysMs = (long)TimeSpan.FromDays(10).TotalMilliseconds;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PluginPath => Environment.GetEnvironmentVariable("NVM_DELAYED_PLUGIN")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvm-labs",
            "rabbitmq_delayed_message_exchange-4.2.0.ez");

    [Fact]
    public async Task DelayedPlugin_TenDayTimeouts_MemoryLimitVisibilityAndRestart()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1" && File.Exists(PluginPath),
            "Set NVM_RUN_LABS=1 and provide the delayed message plugin 4.2.0 (.ez) to run the M7 delayed exchange lab.");
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        await using var rabbit = new ContainerBuilder("rabbitmq:4.2.9-management")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", "lab")
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", password)
            .WithResourceMapping(new FileInfo(PluginPath), "/opt/rabbitmq/plugins/")
            .WithResourceMapping(Encoding.ASCII.GetBytes("[rabbitmq_management,rabbitmq_delayed_message_exchange]."),
                "/etc/rabbitmq/enabled_plugins")
            .WithPortBinding(5672, true).WithPortBinding(15672, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(15672).ForPath("/api/overview")
                .WithBasicAuthentication("lab", password)))
            .Build();
        await rabbit.StartAsync(Ct);
        using var management = new HttpClient { BaseAddress = new Uri($"http://{rabbit.Hostname}:{rabbit.GetMappedPublicPort(15672)}/api/") };
        management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes("lab:" + password)));
        var factory = new ConnectionFactory
        {
            HostName = rabbit.Hostname,
            Port = rabbit.GetMappedPublicPort(5672),
            UserName = "lab",
            Password = password,
        };

        await using (var connection = await factory.CreateConnectionAsync(Ct))
        await using (var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), Ct))
        {
            await channel.ExchangeDeclareAsync(Exchange, "x-delayed-message", durable: true, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-delayed-type"] = "direct" }, cancellationToken: Ct);
            await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: Ct);
            await channel.QueueBindAsync(Queue, Exchange, "due", cancellationToken: Ct);

            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            var before = await MemoryAsync(management);
            var publish = Stopwatch.StartNew();
            for (var i = 0; i < Timeouts; i++)
            { await PublishAsync(channel, i, TenDaysMs); }
            publish.Stop();
            await Task.Delay(TimeSpan.FromSeconds(10), Ct);   // để số đo bộ nhớ ổn định sau đợt ghi
            var after = await MemoryAsync(management);
            var visible = await QueueDepthAsync(management);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"DELAYED timeouts={Timeouts} publish_s={publish.Elapsed.TotalSeconds:F1} mem_before_mb={before / 1e6:F1} mem_after_mb={after / 1e6:F1} per_timeout_bytes={(after - before) / (double)Timeouts:F0} queue_visible={visible}"));
            visible.ShouldBe(0);   // timeout đang chờ không nằm trong queue nào: không liệt kê, không huỷ được bằng API queue

            // Giới hạn delay: x-delay lớn hơn 2^32 - 1 ms (~49,7 ngày) và một delay ngắn làm mốc.
            await PublishAsync(channel, -1, (long)TimeSpan.FromDays(50).TotalMilliseconds);
            await PublishAsync(channel, -2, 2_000);
            await PublishAsync(channel, -3, 30_000);   // phải còn chờ qua lần restart bên dưới
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            var arrived = await DrainAsync(channel);
            output.WriteLine($"DELAYED_LIMIT arrived_within_5s=[{string.Join(',', arrived)}] (-1 = 50 ngày, -2 = 2 giây)");
            arrived.ShouldContain(-2);
        }

        // Restart broker: timeout 30 s đặt trước restart có còn tới không.
        var restart = Stopwatch.StartNew();
        await rabbit.StopAsync(Ct);
        await rabbit.StartAsync(Ct);
        factory.Port = rabbit.GetMappedPublicPort(5672);
        using var management2 = new HttpClient { BaseAddress = new Uri($"http://{rabbit.Hostname}:{rabbit.GetMappedPublicPort(15672)}/api/") };
        management2.DefaultRequestHeaders.Authorization = management.DefaultRequestHeaders.Authorization;
        var restartedMemory = await MemoryAsync(management2);
        await using (var connection = await factory.CreateConnectionAsync(Ct))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
        {
            var survived = new List<int>();
            while (restart.Elapsed < TimeSpan.FromSeconds(60) && !survived.Contains(-3))
            {
                survived.AddRange(await DrainAsync(channel));
                await Task.Delay(1000, Ct);
            }
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"DELAYED_RESTART restart_plus_wait_s={restart.Elapsed.TotalSeconds:F0} mem_after_restart_mb={restartedMemory / 1e6:F1} arrived=[{string.Join(',', survived)}]"));
        }
    }

    private static async Task PublishAsync(IChannel channel, int id, long delayMs)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            Headers = new Dictionary<string, object?> { ["x-delay"] = delayMs },
        };
        // Cỡ một timeout thật: serial, loại timeout, hạn.
        var body = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $$"""{"id":{{id}},"siteId":"NV1","serialNumber":"NV1CL16270A{{Math.Abs(id):D5}}","kind":"AgingDue","dueInMs":{{delayMs}}}"""));
        await channel.BasicPublishAsync(Exchange, "due", mandatory: false, properties, body, Ct);
    }

    private static async Task<List<int>> DrainAsync(IChannel channel)
    {
        var ids = new List<int>();
        while (await channel.BasicGetAsync(Queue, autoAck: true, Ct) is { } message)
        {
            using var json = JsonDocument.Parse(message.Body);
            ids.Add(json.RootElement.GetProperty("id").GetInt32());
        }
        return ids;
    }

    private static async Task<double> MemoryAsync(HttpClient management)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var json = JsonDocument.Parse(await management.GetStringAsync("nodes", Ct));
                return json.RootElement[0].GetProperty("mem_used").GetDouble();
            }
            catch (Exception) when (attempt < 30)
            { await Task.Delay(1000, Ct); }
        }
    }

    private static async Task<int> QueueDepthAsync(HttpClient management)
    {
        using var json = JsonDocument.Parse(await management.GetStringAsync("queues/%2F/" + Uri.EscapeDataString(Queue), Ct));
        return json.RootElement.TryGetProperty("messages", out var messages) ? messages.GetInt32() : 0;
    }
}
