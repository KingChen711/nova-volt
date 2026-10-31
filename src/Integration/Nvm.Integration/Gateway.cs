using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nvm.Integration;

// Gateway (R11): tin ra đi từ MOM tới đây theo tên adapter rồi tới hệ nhận; tin vào tới đây qua Web API, Kafka hoặc file, workflow theo
// message type gọi command của MOM qua API như hệ ngoài thật, rồi trả Ack. Cấu hình adapter và workflow là dữ liệu, không nằm trong code của MOM.

public sealed record OutAdapter(string Name, string Kind, string? Url = null, string? Topic = null, string? Directory = null, string? MailTo = null);

public sealed record InAdapter(string Name, string Kind, string MessageType, string? Topic = null, string? Directory = null, string? AckAdapter = null);

/// <summary>Message type vào → command của MOM. <see cref="PayloadField"/> rỗng thì thân tin chính là thân command.</summary>
public sealed record Workflow(string MessageType, string App, string Command, string? PayloadField = "Payload", string? AckAdapter = null);

public sealed record GatewayConfig(
    IReadOnlyList<OutAdapter> Out,
    IReadOnlyList<InAdapter> In,
    IReadOnlyList<Workflow> Workflows,
    string? ApiKey = null,
    string? KafkaBootstrap = null,
    string? SmtpHost = null,
    int SmtpPort = 25,
    TimeSpan? FilePoll = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static GatewayConfig Load(string path) =>
        JsonSerializer.Deserialize<GatewayConfig>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty.");
}

/// <summary>Đường vào MOM. Test cài bằng executor, chạy thật bằng command API.</summary>
public interface IMomClient
{
    Task<MomReply> CallAsync(string app, string command, string body, CancellationToken cancel);
}

public sealed record MomReply(int StatusCode, string Body)
{
    public bool Ok => StatusCode is >= 200 and < 300;
}

/// <summary>Gọi command API của MOM bằng token lấy từ Keycloak.</summary>
public sealed class HttpMomClient(HttpClient http, Uri mom, Uri tokenEndpoint, string user, string password) : IMomClient, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<MomReply> CallAsync(string app, string command, string body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(mom, $"{app}/odata/{command}"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", await TokenAsync(cancel).ConfigureAwait(false));
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        return new MomReply((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
    }

    private async Task<string> TokenAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_token is null || _expires <= TimeProvider.System.GetUtcNow().AddSeconds(30))
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["client_id"] = "nvm-cli",
                    ["username"] = user,
                    ["password"] = password,
                });
                using var response = await http.PostAsync(tokenEndpoint, form, cancel).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
                _token = document.RootElement.GetProperty("access_token").GetString();
                _expires = TimeProvider.System.GetUtcNow().AddSeconds(document.RootElement.GetProperty("expires_in").GetInt32());
            }

            return _token!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>Kết quả của một tin vào; cũng là nội dung Ack.</summary>
public sealed record InboundResult(string MessageType, bool Ok, int StatusCode, string Detail);

/// <summary>Tìm workflow theo message type, gọi command, gửi Ack nếu workflow có khai.</summary>
public sealed class WorkflowEngine(GatewayConfig config, IMomClient mom, OutboundDispatcher dispatcher, ILogger logger)
{
    public async Task<InboundResult> HandleAsync(string messageType, string content, CancellationToken cancel)
    {
        var workflow = config.Workflows.FirstOrDefault(w => string.Equals(w.MessageType, messageType, StringComparison.OrdinalIgnoreCase));
        if (workflow is null)
        {
            return new InboundResult(messageType, false, 404, $"No workflow for message type {messageType}.");
        }

        var body = workflow.PayloadField is { Length: > 0 } field ? JsonSerializer.Serialize(new Dictionary<string, string> { [field] = content }) : content;
        InboundResult result;
        try
        {
            var reply = await mom.CallAsync(workflow.App, workflow.Command, body, cancel).ConfigureAwait(false);
            result = new InboundResult(messageType, reply.Ok, reply.StatusCode, reply.Body);
        }
        catch (HttpRequestException e)
        {
            result = new InboundResult(messageType, false, 503, $"MOM is not reachable: {e.Message}");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Inbound {Type}: {Status}", messageType, result.StatusCode);
        }

        if (workflow.AckAdapter is { } ack)
        {
            await dispatcher.PostAsync(ack, messageType + "Ack", JsonSerializer.Serialize(new { messageType, ok = result.Ok, status = result.StatusCode, detail = result.Detail }), "application/json", cancel).ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>Gửi tin tới hệ nhận theo tên adapter.</summary>
public sealed class OutboundDispatcher(GatewayConfig config, HttpClient http) : IDisposable
{
    private readonly Lazy<IProducer<string, string>> _producer = new(() =>
        new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = config.KafkaBootstrap }).Build());

    private OutAdapter? Find(string name) => config.Out.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gửi và chờ trả lời của hệ nhận. Chỉ Web API có trả lời; adapter khác trả rỗng sau khi ghi xong.</summary>
    public async Task<(int Status, string Body)> SendAsync(string adapter, string messageType, string content, string contentType, CancellationToken cancel)
    {
        var target = Find(adapter);
        if (target is null)
        {
            return (404, $"Adapter {adapter} is not configured.");
        }

        if (target.Kind == "WebClient")
        {
            using var request = new StringContent(content, Encoding.UTF8, contentType);
            try
            {
                using var response = await http.PostAsync(new Uri(target.Url!), request, cancel).ConfigureAwait(false);
                return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            }
            catch (HttpRequestException e)
            {
                return (502, $"{target.Url} is not reachable: {e.Message}");
            }
        }

        await PostAsync(adapter, messageType, content, contentType, cancel).ConfigureAwait(false);
        return (200, string.Empty);
    }

    public async Task PostAsync(string adapter, string messageType, string content, string contentType, CancellationToken cancel)
    {
        var target = Find(adapter) ?? throw new InvalidOperationException($"Adapter {adapter} is not configured.");
        switch (target.Kind)
        {
            case "WebClient":
                using (var request = new StringContent(content, Encoding.UTF8, contentType))
                using (var response = await http.PostAsync(new Uri(target.Url!), request, cancel).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                }

                break;
            case "File":
                Directory.CreateDirectory(target.Directory!);
                var extension = contentType.Contains("csv", StringComparison.OrdinalIgnoreCase) ? "csv"
                    : contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? "xml"
                    : contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ? "json" : "txt";
                var stamp = TimeProvider.System.GetUtcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                await File.WriteAllTextAsync(Path.Combine(target.Directory!, $"{messageType}_{stamp}_{Guid.NewGuid():N}.{extension}"), content, cancel).ConfigureAwait(false);
                break;
            case "Kafka":
                await _producer.Value.ProduceAsync(target.Topic!, new Message<string, string> { Key = messageType, Value = content }, cancel).ConfigureAwait(false);
                break;
            case "Smtp":
                using (var smtp = new SmtpClient(config.SmtpHost, config.SmtpPort))
                using (var mail = new MailMessage("mom@novavolt.local", target.MailTo!, messageType, content))
                {
                    await smtp.SendMailAsync(mail, cancel).ConfigureAwait(false);
                }

                break;
            default:
                throw new InvalidOperationException($"Adapter {adapter} has unknown kind {target.Kind}.");
        }
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Dispose();
        }
    }
}

/// <summary>Đọc thư mục vào theo chu kỳ. File xử lý xong sang <c>done</c>, lỗi sang <c>error</c>, nên không đọc hai lần.</summary>
public sealed class FileInboundService(GatewayConfig config, WorkflowEngine engine, ILogger<FileInboundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var adapters = config.In.Where(a => a.Kind == "File").ToList();
        if (adapters.Count == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(config.FilePoll ?? TimeSpan.FromSeconds(5));
        do
        {
            foreach (var adapter in adapters)
            {
                await ScanAsync(adapter, stoppingToken).ConfigureAwait(false);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ScanAsync(InAdapter adapter, CancellationToken cancel)
    {
        Directory.CreateDirectory(adapter.Directory!);
        foreach (var path in Directory.EnumerateFiles(adapter.Directory!).Order(StringComparer.Ordinal).ToList())
        {
            var result = await engine.HandleAsync(adapter.MessageType, await File.ReadAllTextAsync(path, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
            var folder = Path.Combine(adapter.Directory!, result.Ok ? "done" : "error");
            Directory.CreateDirectory(folder);
            File.Move(path, Path.Combine(folder, Path.GetFileName(path)), overwrite: true);
            if (!result.Ok)
            {
                logger.LogWarning("File {File} failed: {Detail}", path, result.Detail);
            }
        }
    }
}

/// <summary>Nghe các topic vào; offset commit sau khi workflow chạy xong. Tin lỗi nghiệp vụ không gửi lại: hệ gửi biết qua Ack.</summary>
public sealed class KafkaInboundService(GatewayConfig config, WorkflowEngine engine, OutboundDispatcher dispatcher, ILogger<KafkaInboundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var adapters = config.In.Where(a => a.Kind == "Kafka").ToList();
        if (adapters.Count == 0 || config.KafkaBootstrap is null)
        {
            return;
        }

        await EnsureTopicsAsync().ConfigureAwait(false);
        await Task.WhenAll(adapters.Select(a => Task.Run(() => ConsumeAsync(a, stoppingToken), stoppingToken))).ConfigureAwait(false);
    }

    /// <summary>Broker không tự tạo topic: gateway tạo các topic trong cấu hình lúc khởi động; đã có thì thôi.</summary>
    private async Task EnsureTopicsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = config.KafkaBootstrap }).Build();
        var topics = config.In.Where(a => a.Kind == "Kafka").Select(a => a.Topic).Concat(config.Out.Where(a => a.Kind == "Kafka").Select(a => a.Topic)).Distinct(StringComparer.Ordinal);
        try
        {
            await admin.CreateTopicsAsync(topics.Select(t => new TopicSpecification { Name = t, NumPartitions = 1, ReplicationFactor = 1 })).ConfigureAwait(false);
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            logger.LogDebug("Kafka topics already exist.");
        }
    }

    private async Task ConsumeAsync(InAdapter adapter, CancellationToken cancel)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = config.KafkaBootstrap,
            GroupId = "nvm-gateway-" + adapter.Name,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(adapter.Topic);
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                ConsumeResult<string, string> message;
                try
                {
                    message = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException e)
                {
                    logger.LogWarning("Kafka {Topic}: {Reason}", adapter.Topic, e.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                var result = await engine.HandleAsync(adapter.MessageType, message.Message.Value, cancel).ConfigureAwait(false);
                if (adapter.AckAdapter is { } ack)
                {
                    await dispatcher.PostAsync(ack, adapter.MessageType + "Ack", JsonSerializer.Serialize(new { key = message.Message.Key, ok = result.Ok, status = result.StatusCode }), "application/json", cancel).ConfigureAwait(false);
                }

                consumer.Commit(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Dừng host.
        }
        finally
        {
            consumer.Close();
        }
    }
}

public static class GatewayApp
{
    /// <summary>Dựng gateway. Host thật và test dùng chung; <paramref name="mom"/> là đường vào MOM.</summary>
    public static WebApplication Build(GatewayConfig config, IMomClient mom, string url)
    {
        ArgumentNullException.ThrowIfNull(config);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(mom);
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(100) });
        builder.Services.AddSingleton<OutboundDispatcher>();
        builder.Services.AddSingleton(sp => new WorkflowEngine(config, sp.GetRequiredService<IMomClient>(), sp.GetRequiredService<OutboundDispatcher>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Workflow")));
        builder.Services.AddHostedService<FileInboundService>();
        builder.Services.AddHostedService<KafkaInboundService>();
        var app = builder.Build();

        app.MapPost("/messages/synchronous", async (HttpContext http, OutboundDispatcher dispatcher, string type, string adapter) =>
        {
            var (status, reply) = await dispatcher.SendAsync(adapter, type, await Body(http).ConfigureAwait(false), MediaType(http), http.RequestAborted).ConfigureAwait(false);
            return Results.Text(reply, "application/json", statusCode: status);
        });
        app.MapPost("/messages/asynchronous", async (HttpContext http, OutboundDispatcher dispatcher, string type, string adapter) =>
        {
            await dispatcher.PostAsync(adapter, type, await Body(http).ConfigureAwait(false), MediaType(http), http.RequestAborted).ConfigureAwait(false);
            return Results.Accepted();
        });
        app.MapPost("/api/in/{type}", async (HttpContext http, WorkflowEngine engine, string type) =>
        {
            if (config.ApiKey is { Length: > 0 } key && http.Request.Headers["X-Api-Key"] != key)
            {
                return Results.Unauthorized();
            }

            var result = await engine.HandleAsync(type, await Body(http).ConfigureAwait(false), http.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { result.MessageType, result.Ok, status = result.StatusCode, result.Detail }, statusCode: result.Ok ? 200 : result.StatusCode is 404 ? 404 : 422);
        });
        return app;
    }

    private static string MediaType(HttpContext http) => (http.Request.ContentType ?? "application/json").Split(';')[0].Trim();

    private static async Task<string> Body(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(http.RequestAborted).ConfigureAwait(false);
    }
}
