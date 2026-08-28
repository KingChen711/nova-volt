using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Nvm.Hosting;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;

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
builder.Services.AddSingleton<IMeasurementIngestor, PostgresMeasurementIngestor>();
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
