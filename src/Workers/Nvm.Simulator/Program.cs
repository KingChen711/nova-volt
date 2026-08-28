using System.Globalization;
using Nvm.FactoryModel.Seeding;
using Nvm.Hosting;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Publishing;

// Same reasoning as every other entry point in the repository: deterministic formatting whatever the
// machine's locale says, without InvariantGlobalization. See ADR-020.
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

var options = new SimulatorOptions();
builder.Configuration.GetSection("NVM_SIM").Bind(options);
options.Validate();

// The clock the whole plant runs on. TimeProvider.System in production; a test swaps it and an
// eighteen-hour cycle finishes in milliseconds with the same measurements (K1).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ISparkplugPublisher, MqttSparkplugPublisher>();

builder.Services.AddSingleton(serviceProvider =>
{
    var catalog = FactoryModelSeed.LoadCatalog(
        Path.Combine(builder.Environment.ContentRootPath, options.SeedDirectory));

    var revision = options.Revision ?? catalog.LatestRevision;

    var model = catalog.Find(revision)
        ?? throw new InvalidOperationException(
            $"Revision {revision} is not on the shelf. The catalog holds {string.Join(", ", catalog.Revisions)}.");

    var linePath = EquipmentPath.Parse(options.LinePath);

    return new FormationLine(
        linePath,
        FormationChannels.Under(model, linePath),
        new FormationProfile(options.CycleDuration),
        serviceProvider.GetRequiredService<TimeProvider>().GetUtcNow(),
        options.BirthDeathSequence);
});

builder.Services.AddHostedService<SimulatorWorker>();

await builder.Build().RunAsync();
