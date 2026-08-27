using System.Globalization;
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

        var attributes = Read(received);
        attributes.SpecVersion.ShouldBe("1.0");
        attributes.Type.Value.ShouldBe("com.novavolt.factory-model.revision-activated.v1");
        attributes.Source.Value.ShouldBe("urn:novavolt:nv1:host-all");
        attributes.Time.ShouldBe(KnownTime);
        attributes.DataContentType.ShouldBe("application/json");
    }

    [Fact]
    public async Task AnEventSentStraightToAnEndpoint_IsStampedToo()
    {
        // Publish and Send are separate pipes, and a filter on one does not run on the other. Every
        // event this system emits goes out through Publish, so the send pipe is the one nobody
        // exercises — which is exactly why it is the one that would rot unnoticed. Without this test,
        // deleting the ConfigureSend registration keeps the whole suite green while a direct send to
        // an endpoint carries no CloudEvents metadata at all.
        await using var provider = BuildHarness();
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        // Derived, not hard-coded: renaming the consumer must move this address with it, otherwise the
        // test starts sending into the void and fails for a reason that has nothing to do with filters.
        var endpoint = await harness.Bus.GetSendEndpoint(new Uri(
            harness.Bus.Address,
            DefaultEndpointNameFormatter.Instance.Consumer<RecordingProbeConsumer>()));
        await endpoint.Send(AnEvent(), TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        var attributes = Read(received);
        attributes.SpecVersion.ShouldBe("1.0");
        attributes.Type.Value.ShouldBe("com.novavolt.factory-model.revision-activated.v1");
        attributes.Source.Value.ShouldBe("urn:novavolt:nv1:host-all");
        attributes.DataContentType.ShouldBe("application/json");
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

        Read(received).Id.ShouldBe(KnownEventId);
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

        var source = Read(received).Source;
        source.SiteId.ShouldBe("DE1");
        source.Value.ShouldBe("urn:novavolt:de1:host-all");
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
        CloudEventHeaders.DataContentType.ShouldBe("ce_datacontenttype");
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
        received.Error.ShouldBeNull();
    }

    [Theory]
    [InlineData(CloudEventHeaders.SpecVersion)]
    [InlineData(CloudEventHeaders.Id)]
    [InlineData(CloudEventHeaders.Type)]
    [InlineData(CloudEventHeaders.Source)]
    [InlineData(CloudEventHeaders.Time)]
    [InlineData(CloudEventHeaders.DataContentType)]
    public async Task AMessageMissingAnyOneMandatoryAttribute_IsRefusedRatherThanReadAsAPartialSet(
        string omitted)
    {
        // Every header in turn, because "the set" has no privileged member. Checking one of them first
        // and treating its absence as "this message has no attributes" is exactly the bug this covers:
        // it would let a message missing that one header pass as a message carrying nothing, while the
        // other five sit on it saying otherwise.
        //
        // The headers are written by hand here — the point is a message the real filter would never
        // produce, from a publisher that does not agree with this system about what the set is.
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.AddConsumer<RecordingProbeConsumer>())
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(
            AnEvent(),
            context => StampEveryHeaderExcept(context, omitted),
            TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value.ShouldBeNull();

        var error = received.Error.ShouldNotBeNull();
        error.ShouldBeOfType<InvalidOperationException>();
        error.Message.ShouldContain(omitted);
        error.Message.ShouldContain("5 of the 6");
    }

    [Fact]
    public async Task AHeaderCarryingSomethingOtherThanText_IsMalformedRatherThanAbsent()
    {
        // The type-level twin of the sentinel bug. A header read with `as string` comes back null when
        // it holds anything else, so a message with six headers — one of them an integer — would read
        // as a message with five, and be refused for the wrong reason, or worse, read as one with none.
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.AddConsumer<RecordingProbeConsumer>())
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(
            AnEvent(),
            context =>
            {
                StampEveryHeader(context);
                context.Headers.Set(CloudEventHeaders.Time, 1756090542);
            },
            TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value.ShouldBeNull();
        received.Error.ShouldNotBeNull().Message.ShouldContain(CloudEventHeaders.Time);
    }

    [Fact]
    public async Task AHeaderPresentButEmpty_IsMalformedRatherThanAbsent()
    {
        // CloudEvents treats "no attribute" and "attribute with an empty value" as different claims —
        // the reason the five optional attributes are omitted rather than written blank. Read the other
        // way round, an empty ce_datacontenttype is a publisher asserting an encoding of "", and that
        // must not pass as a well-formed set.
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.AddConsumer<RecordingProbeConsumer>())
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(
            AnEvent(),
            context =>
            {
                StampEveryHeader(context);
                context.Headers.Set(CloudEventHeaders.DataContentType, "   ");
            },
            TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value.ShouldBeNull();
        received.Error.ShouldNotBeNull().Message.ShouldContain(CloudEventHeaders.DataContentType);
    }

    private static CloudEventAttributes Read(ReceivedAttributes received)
    {
        var error = received.Error?.Message;

        // Reported before the null check so that a stamp deleted from the filter fails with the name
        // of the header that went missing, rather than with "received.Value was null".
        error.ShouldBeNull();

        return received.Value.ShouldNotBeNull();
    }

    private static void StampEveryHeader(PublishContext<FactoryModelRevisionActivated> context) =>
        StampEveryHeaderExcept(context, omitted: string.Empty);

    private static void StampEveryHeaderExcept(
        PublishContext<FactoryModelRevisionActivated> context,
        string omitted)
    {
        var message = context.Message;

        var all = new (string Header, string Value)[]
        {
            (CloudEventHeaders.SpecVersion, "1.0"),
            (CloudEventHeaders.Id, message.EventId.ToString()),
            (CloudEventHeaders.Type, EventTypeName.Of(typeof(FactoryModelRevisionActivated)).Value),
            (CloudEventHeaders.Source, EventSource.Create(message.SiteId, "host-all").Value),
            (CloudEventHeaders.Time, message.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
            (CloudEventHeaders.DataContentType, "application/json"),
        };

        foreach (var (header, value) in all.Where(pair => pair.Header != omitted))
        {
            context.Headers.Set(header, value);
        }
    }

    public sealed class ReceivedAttributes
    {
        public CloudEventAttributes? Value { get; set; }

        public Exception? Error { get; set; }
    }

    public sealed class RecordingProbeConsumer(ReceivedAttributes received)
        : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context)
        {
            try
            {
                received.Value = context.CloudEvent();
            }
            catch (InvalidOperationException exception)
            {
                // Recorded, not rethrown: a throw here becomes a fault and five retries, and the
                // assertion is about what the reader refused, not about what the bus did afterwards.
                received.Error = exception;
            }

            return Task.CompletedTask;
        }
    }
}
