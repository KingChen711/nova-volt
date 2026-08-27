using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Nvm.Bus;
using Nvm.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.Host.Infrastructure;
using Nvm.Hosting;
using Nvm.Kernel;
using Serilog;
using Serilog.Events;

const string logTemplate =
    "[{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {Level:u3}] {Message:lj}{NewLine}{Exception}";

// Deterministic formatting regardless of the machine locale.
// This replaces InvariantGlobalization=true, which would have disabled ICU and made
// TimeZoneInfo reject IANA ids such as "Europe/Berlin". See ADR-020.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// Bootstrap logger: captures failures that happen before the host is built,
// which is exactly when configuration mistakes surface.
// formatProvider is explicit on every sink: CA1305 is what enforces the determinism
// that InvariantGlobalization would otherwise have given us.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: logTemplate, formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: logTemplate, formatProvider: CultureInfo.InvariantCulture));

    // One source of truth for ports and credentials: the same .env docker-compose reads.
    // Development only — see DotEnvLoader.
    if (builder.Environment.IsDevelopment())
    {
        var envFile = DotEnvLoader.Load(builder.Environment.ContentRootPath);
        Log.Information("Loaded environment from {EnvFile}", envFile ?? "(none found)");
    }

    builder.Configuration.AddEnvironmentVariables();

    // The only sanctioned clock in the codebase. AGENTS.md K1 forbids DateTime.UtcNow
    // so that day-long sagas stay testable with FakeTimeProvider.
    builder.Services.AddSingleton(TimeProvider.System);

    // Manufacturing Service Bus. Credentials come from the same .env docker-compose reads —
    // one source of truth, no second file holding the same password (M0/C10.2).
    // No consumers here yet: this host publishes, and the probe worker consumes.
    builder.Services.AddNvmBus(bus =>
    {
        // Host stays at its default of localhost, the same assumption every other dependency in
        // this host makes (see HealthCheckRegistration). .env is the port and credential table;
        // it does not carry host names.
        bus.Port = ushort.Parse(DotEnvLoader.Required("NVM_PORT_RABBITMQ"), CultureInfo.InvariantCulture);
        bus.Username = DotEnvLoader.Required("NVM_RABBITMQ_USER");
        bus.Password = DotEnvLoader.Required("NVM_RABBITMQ_PASSWORD");
        // Names this deployable in every CloudEvents source it publishes:
        // urn:novavolt:nv1:host-all. Dev mode runs every App in one process (scope.md §5.3).
        bus.ApplicationName = "host-all";
    });

    // Command pipeline plus the first Functional Block. The kernel is told which assemblies to scan
    // rather than scanning everything loaded: a Functional Block that never announced itself should
    // not be wired up because it happened to be in the output directory.
    builder.Services.AddNvmKernel(typeof(ActivateFactoryModelRevisionCommand).Assembly);
    builder.Services.AddNvmFactoryModel(SeedFileLocator.Locate(builder.Environment.ContentRootPath));

    builder.Services.AddDependencyHealthChecks();

    var app = builder.Build();

    app.UseSerilogRequestLogging(options =>
        options.GetLevel = (httpContext, _, exception) => exception is not null
            ? LogEventLevel.Error
            : httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
                // Health probes run every few seconds; logging them at Information
                // buries everything else.
                : httpContext.Request.Path.StartsWithSegments("/health")
                    ? LogEventLevel.Verbose
                    : LogEventLevel.Information);

    app.MapGet("/", (TimeProvider clock, IHostEnvironment environment) => new
    {
        Name = "NovaVolt MES — Host.All",
        Version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown",
        Environment = environment.EnvironmentName,
        UtcNow = clock.GetUtcNow(),
    });

    // Liveness and readiness are deliberately separate.
    //   live  = "the process is alive, do not restart me"
    //   ready = "my dependencies are reachable, send me traffic"
    // Merging them makes an orchestrator kill a healthy process whenever a
    // database is briefly slow.
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains(HealthTags.Live),
        ResponseWriter = HealthReportWriter.Write,
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains(HealthTags.Ready),
        ResponseWriter = HealthReportWriter.Write,
    });

    // Same rule as DotEnvLoader: a laptop convenience that must not exist anywhere else. These
    // endpoints publish events on request, which is a tool in development and an unguarded write path
    // into the plant's event stream in any other environment.
    if (app.Environment.IsDevelopment())
    {
        app.MapDevBusEndpoints();
    }

    app.Run();
    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "Host terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
