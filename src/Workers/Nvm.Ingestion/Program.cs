using System.Globalization;
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

if (options.FileDrop.Enabled)
{
    // Same ingestor, same transaction, same natural key. The adapter differs only in how it reads
    // (C15.1) — a second dedup definition would drift from this one within months.
    var seedDirectory = Path.Combine(builder.Environment.ContentRootPath, options.SeedDirectory);

    builder.Services.AddSingleton(options.FileDrop);
    builder.Services.AddSingleton(SeededEquipmentDirectory.Load(seedDirectory, options.Revision));
    builder.Services.AddSingleton<CsvMeasurementReader>();
    builder.Services.AddSingleton<FileDropProcessor>();
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
