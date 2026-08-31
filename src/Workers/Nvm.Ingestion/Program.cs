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
    // The WORM half of C12, finally reachable from a running service rather than only from a test.
    // ForcePathStyle because MinIO serves buckets as a path segment; the region is a formality the
    // signer insists on and MinIO ignores.
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

// Refuse to start rather than run a service that eats exports and keeps no originals. The previous
// version passed a null archive into the processor and said so in a comment — which is a deployment
// quietly losing the bytes an auditor is entitled to ask for, wearing the shape of a note (C12.1).
if (options.FileDrop.Enabled && !options.RawCurveArchive.Enabled)
{
    throw new InvalidOperationException(
        "NVM_INGEST__FileDrop__Enabled is true while NVM_INGEST__RawCurveArchive__Enabled is false. "
        + "The file drop consumes an export and moves it out of the inbox, so with no archive its "
        + "original bytes are gone for good. Enable the archive, or disable the file drop.");
}

if (options.FileDrop.Enabled)
{
    // Same ingestor, same transaction, same natural key. The adapter differs only in how it reads
    // (C15.1) — a second dedup definition would drift from this one within months.
    var seedDirectory = Path.Combine(builder.Environment.ContentRootPath, options.SeedDirectory);

    builder.Services.AddSingleton(options.FileDrop);
    builder.Services.AddSingleton(SeededEquipmentDirectory.Load(seedDirectory, options.Revision));
    builder.Services.AddSingleton<CsvMeasurementReader>();

    // Built by hand rather than by convention, and now with GetRequiredService: the guard above has
    // already refused to start without an archive, so resolving it as optional here would only
    // re-open the hole one refactor later.
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
    // The M1 bus, unchanged. Ingestion adds no consumers — it only publishes — and it reaches
    // RabbitMQ on its it-net leg, never from dmz-net (K11).
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

// The limiter only applies where a policy is attached, which is the batch endpoint alone. Health
// checks stay outside it: a saturated ingestion must still answer /health/ready, or Docker would
// restart the container for the sin of being busy and turn backpressure into an outage.
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
