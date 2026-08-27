using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nvm.Bus.CloudEvents;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.UnitTests.Bus;

public sealed class CloudEventHeaderTests
{
    private static readonly Guid KnownEventId = Guid.Parse("0198f3a1-7c2e-5b4d-9e11-3a7f2c9b0d44");

    private static readonly DateTimeOffset KnownTime =
        new(2026, 8, 25, 3, 15, 42, 128, TimeSpan.Zero);

    private static FactoryModelRevisionActivated AnEvent(string siteId = "NV1") =>
        new(KnownEventId, KnownTime, siteId, 1, 41, [], []);

    private static async Task<ITestHarness> StartHarnessAsync(ServiceProvider provider)
    {
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        return harness;
    }

    private static ServiceProvider BuildHarness() =>
        new ServiceCollection()
            .AddMassTransitTestHarness(bus =>
            {
                bus.AddConsumer<RecordingProbeConsumer>();

                // The real filter, over the in-memory transport. Stamping the headers in the test
                // instead would leave a test that still passes with the filter deleted.
                bus.UsingInMemory((context, configurator) =>
                {
                    configurator.UseNvmCloudEvents("host-all");
                    configurator.ConfigureEndpoints(context);
                });
            })
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);

    [Fact]
    public async Task EveryOutgoingEvent_CarriesTheMandatoryCloudEventsAttributes()
    {
        // The point of the whole commit: the §7.4 envelope is not only in the documentation. An
        // operator with rabbitmqadmin, a bridge to another system, or a message sitting in an error
        // queue that no code could deserialize can all still see what the message claims to be.
        await using var provider = BuildHarness();
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(AnEvent(), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        var attributes = received.Value.ShouldNotBeNull();
        attributes.SpecVersion.ShouldBe("1.0");
        attributes.Type.Value.ShouldBe("com.novavolt.factory-model.revision-activated.v1");
        attributes.Source.Value.ShouldBe("urn:novavolt:nv1:host-all");
        attributes.Time.ShouldBe(KnownTime);
    }

    [Fact]
    public async Task CloudEventId_IsTheEventIdAndThereforeTheCommandsIdempotencyKey()
    {
        // The join between the two deduplication layers, checked on the wire rather than in a record.
        // Ingestion drops repeated device messages by this value and the command pipeline drops
        // repeated commands by it; a second source for it would be a second thing to drift.
        await using var provider = BuildHarness();
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(AnEvent(), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value!.Id.ShouldBe(KnownEventId);
    }

    [Fact]
    public async Task Source_NamesThePlantTheEventCameFromNotTheProcessesDefault()
    {
        // Source is site plus application, and the site comes off the payload. A process publishing
        // for both plants must not stamp every message with whichever one it started with.
        await using var provider = BuildHarness();
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(AnEvent("DE1"), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value!.Source.SiteId.ShouldBe("DE1");
        received.Value.Source.Value.ShouldBe("urn:novavolt:de1:host-all");
    }

    [Fact]
    public void HeaderNames_UseTheKafkaStylePrefixThisSystemChose()
    {
        // AMQP 0-9-1 has no CloudEvents binding, so this prefix is a local convention rather than a
        // standard. Pinned here so it cannot drift, and explained in ADR-008.
        CloudEventHeaders.SpecVersion.ShouldBe("ce_specversion");
        CloudEventHeaders.Id.ShouldBe("ce_id");
        CloudEventHeaders.Type.ShouldBe("ce_type");
        CloudEventHeaders.Source.ShouldBe("ce_source");
        CloudEventHeaders.Time.ShouldBe("ce_time");
    }

    [Fact]
    public async Task AMessageWithNoCloudEventsHeaders_ReadsAsNullRatherThanThrowing()
    {
        // Not every message on a bus comes from this system's publish path. Absent attributes are a
        // fact about the message, not a failure to report — so this harness deliberately has no filter.
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.AddConsumer<RecordingProbeConsumer>())
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(AnEvent(), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value.ShouldBeNull();
    }

    public sealed class ReceivedAttributes
    {
        public CloudEventAttributes? Value { get; set; }
    }

    public sealed class RecordingProbeConsumer(ReceivedAttributes received)
        : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context)
        {
            received.Value = context.CloudEvent();

            return Task.CompletedTask;
        }
    }
}
