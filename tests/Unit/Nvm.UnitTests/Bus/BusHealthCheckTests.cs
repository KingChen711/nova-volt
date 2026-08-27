using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Nvm.Bus;
using Nvm.Hosting;

namespace Nvm.UnitTests.Bus;

public sealed class BusHealthCheckTests
{
    // Async because MassTransit registers an IAsyncDisposable singleton, and disposing the container
    // synchronously throws rather than falling back.
    private static async Task<HealthCheckRegistration> BusRegistrationAsync()
    {
        var services = new ServiceCollection();

        services.AddNvmBus(bus =>
        {
            bus.Username = "nvm";
            bus.Password = "irrelevant";
            bus.ApplicationName = "unit-tests";
        });

        // Nothing here connects to a broker: AddNvmBus only builds registrations, and the bus is
        // started later by a hosted service. That is the same property the outage lab depends on.
        await using var provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Single(registration => registration.Name == BusServiceCollectionExtensions.HealthCheckName);
    }

    [Fact]
    public async Task AddNvmBus_RegistersTheBusProbeUnderThisSystemsName()
    {
        // MassTransit calls it "masstransit-bus" if nobody says otherwise. A probe name appears in
        // dashboards, alerts and runbooks, so it is stated rather than inherited from whichever
        // version of the library happens to be pinned.
        (await BusRegistrationAsync()).Name.ShouldBe("bus");
    }

    [Fact]
    public async Task AddNvmBus_TagsTheBusProbeReadyAndNothingElse()
    {
        // The assertion that matters is the second half: NOT live. A bus that cannot reach its broker
        // is a bus that must stop taking traffic, not a process that must be restarted — and an
        // orchestrator restarting every instance during a broker outage is precisely what N15 forbids.
        (await BusRegistrationAsync()).Tags.ShouldBe([HealthTags.Ready]);
    }

    [Fact]
    public async Task AddNvmBus_FailsTheBusProbeAsUnhealthyRatherThanDegraded()
    {
        // Degraded answers HTTP 200, which would leave the instance in rotation while its bus cannot
        // carry a message. This test exists because that difference is invisible in the JSON body and
        // shows up only as a status code nobody reads until an incident.
        (await BusRegistrationAsync()).FailureStatus.ShouldBe(HealthStatus.Unhealthy);
    }
}
