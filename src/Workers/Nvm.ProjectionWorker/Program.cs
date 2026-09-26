using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.SqlClient;
using Npgsql;
using Nvm.Bus;
using Nvm.Hosting;
using Nvm.Projections;
using Nvm.ProjectionWorker;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
if (args.Contains("--health-probe", StringComparer.Ordinal))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    { Environment.ExitCode = 1; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
string Required(string key) => builder.Configuration[key] is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"{key} is required.");

if (args.Contains("--connect-pom", StringComparer.Ordinal))
{
    await using var dataSource = NpgsqlDataSource.Create(Required("NVM_PROJECTIONS:MigrationConnectionString"));
    await ProjectionPomConnector.ConnectAsync(dataSource);
    return;
}
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await ProjectionStorageSetup.PrepareAsync(
        Required("NVM_PROJECTIONS:MigrationConnectionString"),
        Required("NVM_PROJECTIONS:SqlMigrationConnectionString"),
        Required("NVM_PROJECTION_PG_PASSWORD"), Required("NVM_PROJECTION_SQL_PASSWORD"));
    return;
}

var sqlConnection = Required("NVM_PROJECTIONS:SqlConnectionString");
var postgresConnection = Required("NVM_PROJECTIONS:ConnectionString");
var sites = Required("NVM_PROJECTIONS:Sites").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (args.Contains("--reconcile", StringComparer.Ordinal))
{
    await using var store = NpgsqlDataSource.Create(postgresConnection);
    var projection = new ProductionUnitProjection(store, new SqlGlobalEventFeed(sqlConnection));
    var genealogy = new GenealogyProjection(store, new SqlGlobalEventFeed(sqlConnection));
    var ordered = new OrderedProjectionRunner(store, new SqlGlobalEventFeed(sqlConnection), [new BinInventoryProjection()]);
    foreach (var site in sites)
    {
        await projection.CatchUpAsync(site);
        await genealogy.CatchUpAsync(site);
        await ordered.CatchUpAsync(site);
    }
    return;
}
if (args.Contains("--rebuild-genealogy", StringComparer.Ordinal))
{
    // Xoá read model cần quyền chủ schema; runtime role không có quyền DELETE trên bảng cạnh.
    await using var owner = NpgsqlDataSource.Create(Required("NVM_PROJECTIONS:MigrationConnectionString"));
    var genealogy = new GenealogyProjection(owner, new SqlGlobalEventFeed(sqlConnection));
    foreach (var site in sites)
    {
        var report = await genealogy.RebuildAsync(site);
        Console.WriteLine($"GENEALOGY_REBUILD site={report.SiteId} facts={report.FactsApplied} seconds={report.Elapsed.TotalSeconds:F1}");
    }
    return;
}
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnection));
builder.Services.AddSingleton(new SqlGlobalEventFeed(sqlConnection));
builder.Services.AddSingleton<ProductionUnitProjectionInbox>();
builder.Services.AddSingleton<GenealogyProjectionInbox>();
builder.Services.AddSingleton<IOrderedProjection, BinInventoryProjection>();
builder.Services.AddSingleton<IGlobalEventFeed>(service => service.GetRequiredService<SqlGlobalEventFeed>());
builder.Services.AddSingleton<OrderedProjectionRunner>();
builder.Services.AddHostedService(service => new OrderedProjectionWorker(
    service.GetRequiredService<OrderedProjectionRunner>(), sites,
    service.GetRequiredService<TimeProvider>(), service.GetRequiredService<ILogger<OrderedProjectionWorker>>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService(service => new ProductionUnitProjectionWorker(
    service.GetRequiredService<ProductionUnitProjectionInbox>(), sites,
    service.GetRequiredService<TimeProvider>(), service.GetRequiredService<ILogger<ProductionUnitProjectionWorker>>()));
builder.Services.AddHostedService(service => new GenealogyProjectionWorker(
    service.GetRequiredService<GenealogyProjectionInbox>(), sites,
    service.GetRequiredService<TimeProvider>(), service.GetRequiredService<ILogger<GenealogyProjectionWorker>>()));
builder.Services.AddNvmBus(bus =>
{
    bus.Host = Required("NVM_RABBITMQ_HOST");
    bus.Port = ushort.Parse(Required("NVM_PORT_RABBITMQ"), CultureInfo.InvariantCulture);
    bus.Username = Required("NVM_RABBITMQ_USER");
    bus.Password = Required("NVM_RABBITMQ_PASSWORD");
    bus.ApplicationName = "unit-projection";
}, consumers =>
{
    consumers.AddNvmConsumer<ProductionUnitProjectionConsumer>();
    consumers.AddNvmConsumer<GenealogyProjectionConsumer>();
    consumers.AddNvmConsumer<OrderedProjectionsConsumer>();
});
builder.Services.AddHealthChecks()
    .AddCheck<ProjectionStoreHealthCheck>("projection-store", tags: [HealthTags.Ready])
    .AddAsyncCheck("event-source", async cancellationToken =>
    {
        try
        {
            await using var connection = new SqlConnection(sqlConnection);
            await connection.OpenAsync(cancellationToken);
            using var query = new SqlCommand("SELECT TOP (0) SourceEventId, CloudEventJson FROM es.Events;", connection);
            using var reader = await query.ExecuteReaderAsync(cancellationToken);
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        }
        catch (SqlException)
        { return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Event source is unavailable or not readable."); }
    }, tags: [HealthTags.Ready]);
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok());
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{ Predicate = registration => registration.Tags.Contains(HealthTags.Ready) });
await app.RunAsync();
