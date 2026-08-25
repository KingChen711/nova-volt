using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

    // The only sanctioned clock in the codebase. AGENTS.md K1 forbids DateTime.UtcNow
    // so that day-long sagas stay testable with FakeTimeProvider.
    builder.Services.AddSingleton(TimeProvider.System);

    builder.Services
        .AddHealthChecks()
        .AddCheck(
            "self",
            () => HealthCheckResult.Healthy("Process is running."),
            tags: ["live"]);

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
        Predicate = registration => registration.Tags.Contains("live"),
    });

    // No readiness checks are registered yet; C10 adds one per dependency.
    // An empty predicate set reports Healthy, which is correct: nothing to wait for.
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready"),
    });

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
