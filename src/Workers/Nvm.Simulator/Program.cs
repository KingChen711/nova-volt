using System.Globalization;
using Nvm.FactoryModel.Seeding;
using Nvm.Hosting;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Publishing;

// Cùng lý do như mọi entry point khác trong repo: định dạng tất định (deterministic) bất kể locale
// của máy nói gì, mà không cần InvariantGlobalization. Xem ADR-020.
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

// Đồng hồ mà cả nhà máy chạy theo. TimeProvider.System ở production; một test sẽ thay nó bằng đồng
// hồ khác và một cycle mười tám giờ sẽ hoàn thành trong vài mili-giây với cùng những measurement (K1).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);

// Broker client, và fault injector bọc quanh nó. Injector luôn nằm trên đường đi, kể cả khi mọi rate
// đều bằng không: run report khi đó sẽ nói rõ các fault đã làm gì thay vì để việc đó phải được suy ra
// từ chuyện có ai nhớ bật chúng lên hay không (R-M2-1).
builder.Services.AddSingleton<MqttSparkplugPublisher>();
builder.Services.AddSingleton(serviceProvider => new FaultInjectingPublisher(
    serviceProvider.GetRequiredService<MqttSparkplugPublisher>(),
    options.Faults,
    serviceProvider.GetRequiredService<TimeProvider>(),
    serviceProvider.GetRequiredService<ILogger<FaultInjectingPublisher>>()));

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
        options.BirthDeathSequence,
        new DeviceClockDrift(options.Faults.DriftedDeviceRate, options.Faults.ClockDrift));
});

builder.Services.AddHostedService<SimulatorWorker>();

await builder.Build().RunAsync();
