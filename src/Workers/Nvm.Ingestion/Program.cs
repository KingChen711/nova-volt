using System.Globalization;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Nvm.Bus;
using Nvm.FactoryModel.Seeding;
using Nvm.Hosting;
using Nvm.Ingestion;
using Nvm.Ingestion.FileDrop;
using Nvm.Ingestion.Persistence;
using Nvm.Ingestion.Publishing;
using Nvm.Ingestion.RawCurves;

if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args);
}

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

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

var options = IngestionOptions.FromConfiguration(builder.Configuration);

if (args.Any(argument => string.Equals(argument, "--migrate", StringComparison.Ordinal)))
{
    IngestionSchemaMigrator.Upgrade(options.ConnectionString);
    return 0;
}

builder.WebHost.UseUrls(options.ListenUrl);

var dataSource = NpgsqlDataSource.Create(options.ConnectionString);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<IngestionMetrics>();
builder.Services.AddSingleton<IngestionLag>();
builder.Services.AddSingleton(new PublishedSignals(options.PublishedSignals));

if (options.RawCurveArchive.Enabled)
{
    // Nửa WORM của C12, cuối cùng cũng chạm tới được từ một service đang chạy chứ không chỉ từ một
    // test. ForcePathStyle vì MinIO phục vụ bucket dưới dạng một path segment; region là một thủ tục
    // hình thức mà signer khăng khăng đòi và MinIO thì bỏ qua.
    builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
        new BasicAWSCredentials(options.RawCurveArchive.AccessKey, options.RawCurveArchive.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = options.RawCurveArchive.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        }));
    builder.Services.AddSingleton<IRawCurveArchive>(services => new RawCurveArchiveStore(
        services.GetRequiredService<NpgsqlDataSource>(),
        services.GetRequiredService<IAmazonS3>(),
        services.GetRequiredService<TimeProvider>(),
        options.RawCurveArchive.BucketName));
}

// Từ chối khởi động thay vì chạy một service nuốt export mà không giữ bản gốc nào. Phiên bản trước
// truyền một archive null vào processor và nói vậy trong một comment — đó là một deployment âm thầm
// làm mất những byte mà một auditor có quyền hỏi tới, khoác lên hình dạng của một ghi chú (C12.1).
if (options.FileDrop.Enabled && !options.RawCurveArchive.Enabled)
{
    throw new InvalidOperationException(
        "NVM_INGEST__FileDrop__Enabled is true while NVM_INGEST__RawCurveArchive__Enabled is false. "
        + "The file drop consumes an export and moves it out of the inbox, so with no archive its "
        + "original bytes are gone for good. Enable the archive, or disable the file drop.");
}

if (options.FileDrop.Enabled)
{
    // Cùng ingestor, cùng transaction, cùng natural key. Adapter chỉ khác ở cách nó đọc (C15.1) —
    // một định nghĩa dedup thứ hai sẽ trôi dạt khỏi cái này chỉ sau vài tháng.
    var seedDirectory = Path.Combine(builder.Environment.ContentRootPath, options.SeedDirectory);

    builder.Services.AddSingleton(options.FileDrop);
    builder.Services.AddSingleton(SeededEquipmentDirectory.Load(seedDirectory, options.Revision));
    builder.Services.AddSingleton<CsvMeasurementReader>();

    // Xây bằng tay thay vì bằng convention, và giờ dùng GetRequiredService: guard ở trên đã từ chối
    // khởi động khi không có archive rồi, nên resolve nó như optional ở đây chỉ mở lại lỗ hổng đó
    // sau một lần refactor nữa mà thôi.
    builder.Services.AddSingleton(services => new FileDropProcessor(
        services.GetRequiredService<CsvMeasurementReader>(),
        services.GetRequiredService<IMeasurementIngestor>(),
        services.GetRequiredService<FileDropOptions>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILogger<FileDropProcessor>>(),
        services.GetRequiredService<IRawCurveArchive>()));
    builder.Services.AddHostedService<FileDropWatcher>();
}

if (options.PublishesToBus)
{
    // M1 bus, không đổi. Ingestion không thêm consumer nào — nó chỉ publish — và nó tiếp cận
    // RabbitMQ qua chân it-net của nó, không bao giờ từ dmz-net (K11).
    builder.Services.AddNvmBus(bus =>
    {
        bus.Host = options.BusHost;
        bus.Port = options.BusPort;
        bus.Username = options.BusUsername;
        bus.Password = options.BusPassword;
        bus.ApplicationName = "ingestion";
    });

    builder.Services.AddSingleton<IMeasurementEventPublisher, BusMeasurementEventPublisher>();
}
else
{
    builder.Services.AddSingleton<IMeasurementEventPublisher>(NullMeasurementEventPublisher.Instance);
}

builder.Services.AddSingleton<IMeasurementIngestor>(services => new PostgresMeasurementIngestor(
    services.GetRequiredService<NpgsqlDataSource>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<IngestionMetrics>(),
    services.GetRequiredService<IngestionLag>(),
    services.GetRequiredService<IMeasurementEventPublisher>(),
    services.GetRequiredService<PublishedSignals>(),
    options.ClockDriftThreshold,
    options.WriterParallelism,
    options.MinRowsPerWriter));
builder.Services.AddIngestionAdmissionControl(options);
builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthTags.Live])
    .AddCheck<PostgresReadinessCheck>("postgres", tags: [HealthTags.Ready]);

var app = builder.Build();

// Limiter chỉ áp dụng ở nơi có gắn policy, tức là chỉ mỗi batch endpoint. Health check nằm ngoài
// phạm vi đó: một ingestion đang quá tải vẫn phải trả lời /health/ready, nếu không Docker sẽ restart
// container chỉ vì tội đang bận, và biến backpressure thành một outage.
app.UseRateLimiter();

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(HealthTags.Live) });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(HealthTags.Ready) });
IngestionEndpoints.Map(app);

await app.RunAsync();
return 0;
