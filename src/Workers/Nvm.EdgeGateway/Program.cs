using System.Globalization;
using MQTTnet;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Buffering;
using Nvm.EdgeGateway.Decoding;
using Nvm.EdgeGateway.Forwarding;
using Nvm.EdgeGateway.Sessions;
using Nvm.Hosting;
using Nvm.Kernel.Identity;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = Host.CreateApplicationBuilder(args);

if (builder.Environment.IsDevelopment())
{
    DotEnvLoader.Load(builder.Environment.ContentRootPath);
}

builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
    options.UseUtcTimestamp = true;
});

var options = new EdgeGatewayOptions();
builder.Configuration.GetSection("NVM_EDGE").Bind(options);
options.Validate();

if (BufferCrashProbe.IsRequested(args))
{
    return await BufferCrashProbe.RunAsync(args, options.Buffer);
}

var seedDirectory = Path.Combine(builder.Environment.ContentRootPath, options.SeedDirectory);
var equipmentDirectory = GatewayEquipmentDirectory.Load(seedDirectory, options.Revision);
var httpClient = new HttpClient { Timeout = options.RequestTimeout };

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.Buffer);
builder.Services.AddSingleton<IEquipmentDirectory>(equipmentDirectory);
builder.Services.AddSingleton(httpClient);
builder.Services.AddSingleton<IMqttClient>(_ => new MqttClientFactory().CreateMqttClient());
builder.Services.AddSingleton<GatewayCounters>();
builder.Services.AddSingleton<NodeSessionTracker>();
builder.Services.AddSingleton<GatewaySessionMetrics>();
builder.Services.AddSingleton<SparkplugMessageDecoder>();
builder.Services.AddHostedService<RebirthRequestPump>();
builder.Services.AddSingleton<FileStoreAndForwardBuffer>();
builder.Services.AddSingleton<GatewayBufferMetrics>();
builder.Services.AddSingleton<GatewayFlushMetrics>();
builder.Services.AddHostedService<GatewayDiagnosticsWriter>();
builder.Services.AddSingleton<FlushRateLimiter>();
// Seeded per process, not per message: what must differ is one gateway's schedule from the next
// gateway's, and Random.Shared already differs across processes.
builder.Services.AddSingleton(services => new FlushBackoff(
    services.GetRequiredService<PersistentBufferOptions>(),
    Random.Shared));
builder.Services.AddSingleton<BufferWritePump>();
builder.Services.AddSingleton<IGatewayBufferWriter>(services => services.GetRequiredService<BufferWritePump>());
builder.Services.AddHostedService(services => services.GetRequiredService<BufferWritePump>());
builder.Services.AddSingleton<IGatewayBatchSink, HttpGatewayBatchSink>();
builder.Services.AddHostedService<StoreAndForwardFlusher>();
builder.Services.AddHostedService<EdgeGatewayWorker>();

await builder.Build().RunAsync();
return 0;
