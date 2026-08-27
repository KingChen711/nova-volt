using System.Globalization;
using Nvm.Bus;
using Nvm.BusProbe;
using Nvm.BusProbe.Consumers;
using Nvm.Hosting;

// Same reasoning as Nvm.Host.All: deterministic formatting regardless of machine locale, without
// InvariantGlobalization. See ADR-020.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = Host.CreateApplicationBuilder(args);

// One source of truth for ports and credentials: the same .env docker-compose reads.
// Development only — see DotEnvLoader.
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

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddNvmBus(
    bus =>
    {
        bus.Port = ushort.Parse(DotEnvLoader.Required("NVM_PORT_RABBITMQ"), CultureInfo.InvariantCulture);
        bus.Username = DotEnvLoader.Required("NVM_RABBITMQ_USER");
        bus.Password = DotEnvLoader.Required("NVM_RABBITMQ_PASSWORD");
        // This worker never publishes, so the name only reaches the log. It is stated anyway: an
        // application name that is only filled in where it is used is the one that is wrong on the
        // day something starts publishing.
        bus.ApplicationName = "bus-probe";
    },
    consumers =>
    {
        // Two registrations, two receive endpoints, two queues. Register both on one endpoint and
        // each message reaches exactly one of them — competing consumers, not fan-out, and the
        // difference does not show up until somebody notices half the audit trail is missing.
        if (ProbeSelection.Includes(ProbeSelection.Cache))
        {
            consumers.AddNvmConsumer<FactoryModelCacheProbe>();
        }

        if (ProbeSelection.Includes(ProbeSelection.Audit))
        {
            consumers.AddNvmConsumer<FactoryModelAuditProbe>();
        }

        // Off unless asked for. See ProbeSelection.
        if (ProbeSelection.Includes(ProbeSelection.Failing))
        {
            consumers.AddNvmConsumer<FailingProbe>();
        }
    });

var host = builder.Build();

var startup = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Nvm.BusProbe");

// Read before the call: Describe() goes to the environment, and CA1873 is right that an argument
// doing that inside a log call pays for it whether or not the line is written.
var roles = ProbeSelection.Describe();

StartupLog.Starting(startup, roles);

// A worker that fails every message on purpose is worth a warning rather than a line in the middle of
// the startup noise — somebody who left the switch on wants to be told, not to find out from an error
// queue tomorrow.
if (ProbeSelection.Includes(ProbeSelection.Failing))
{
    StartupLog.FailingConsumerIsOn(startup);
}

await host.RunAsync();
