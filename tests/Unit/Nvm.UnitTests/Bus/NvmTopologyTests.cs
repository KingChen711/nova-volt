using MassTransit;
using Nvm.Bus;
using Nvm.Bus.Topology;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.UnitTests.Bus;

public sealed class NvmTopologyTests
{
    private static readonly EventTypeName RevisionActivated =
        EventTypeName.Of(typeof(FactoryModelRevisionActivated));

    [Fact]
    public void ExchangeFor_IsNamedAfterTheBoundedContextNotTheMessageType()
    {
        // One exchange per context, not per event. A context has an owner and a lifetime; a message
        // type does not, and adding a fourth event to Traceability should not add a fourth exchange
        // for every consumer to discover.
        NvmTopology.ExchangeFor(RevisionActivated).ShouldBe("nvm.factory-model");
        NvmTopology.ExchangeFor("traceability").ShouldBe("nvm.traceability");
    }

    [Fact]
    public void EventTypeName_IsReadFromTheAttributesRatherThanTheClassName()
    {
        // The wire name is stated in [EventContract], not derived from FactoryModelRevisionActivated.
        // Deriving it would turn an IDE rename into a topology change that nothing warns about.
        RevisionActivated.Value.ShouldBe("com.novavolt.factory-model.revision-activated.v1");
    }

    [Fact]
    public void BindingForSite_SubscribesToOnePlantAndNothingElse()
    {
        // Multiplant falls out of the routing key rather than out of a filter in every consumer: a
        // service bound here never receives a Leipzig message, so it cannot leak one by forgetting to
        // check (AGENTS.md K3).
        NvmTopology.BindingForSite("NV1").ShouldBe("nvm.NV1.#");
    }

    [Fact]
    public void BindingForContext_NarrowsToOneContextAtOnePlant()
    {
        NvmTopology.BindingForContext("DE1", "quality").ShouldBe("nvm.DE1.quality.#");
    }

    [Fact]
    public void BindingForEventAtEverySite_UsesTheSingleSegmentWildcard()
    {
        // '*' matches exactly one segment, '#' matches the rest. Using '#' for the site would also
        // match every other event in the system, delivering all of them to a consumer that asked for
        // one.
        NvmTopology.BindingForEventAtEverySite(RevisionActivated)
            .ShouldBe("nvm.*.factory-model.revision-activated.v1");
    }

    [Fact]
    public void BindingForEvent_IsExactlyTheRoutingKeyThePublisherUses()
    {
        // The narrowest binding is the key itself. Asserting they are the same string is the check
        // that a consumer's subscription and a publisher's address cannot drift apart.
        NvmTopology.BindingForEvent("NV1", RevisionActivated)
            .ShouldBe(RoutingKey.Create("NV1", RevisionActivated).Value);
    }

    [Theory]
    [InlineData("nv1")]
    [InlineData("Nv1")]
    [InlineData("NV-1")]
    public void Binding_WithASiteThatIsNotUpperCase_IsRefused(string siteId)
    {
        // The trap the whole convention exists to close. AMQP compares routing keys byte for byte, so
        // a publisher on nvm.NV1.* and a consumer bound to nvm.nv1.# never meet — and the broker
        // reports nothing at all. Refusing here is the only moment anybody finds out.
        Should.Throw<FormatException>(() => NvmTopology.BindingForSite(siteId));
    }

    [Fact]
    public void EndpointNameFormatter_NamesAQueueAfterWhatTheConsumerIsFor()
    {
        NvmEndpointNameFormatter.Instance.Consumer<CacheUpdaterProbe>().ShouldBe("nvm.factory-model.cache-updater");
    }

    [Fact]
    public void EndpointNameFormatter_RefusesAConsumerThatHasNotDeclaredItsQueue()
    {
        // MassTransit would otherwise name the queue after the class. The rename that follows is
        // correct, the build stays green, and the service quietly starts reading from a new empty
        // queue while the old one holds messages nobody will process.
        var thrown = Should.Throw<InvalidOperationException>(
            () => NvmEndpointNameFormatter.Instance.Consumer<UndeclaredProbe>());

        thrown.Message.ShouldContain(nameof(UndeclaredProbe));
        thrown.Message.ShouldContain("BusEndpoint");
    }

    [Fact]
    public void BusOptions_WithoutCredentials_AreRefusedWhileTheContainerIsBuilt()
    {
        // Fail at startup as one clear line, rather than two hours later as a consumer that never
        // received anything and a broker log nobody was watching.
        var options = new NvmBusOptions { Username = "nvm", Password = "" };

        var thrown = Should.Throw<InvalidOperationException>(options.Validate);

        thrown.Message.ShouldContain("NVM_RABBITMQ_USER");
    }

    [Fact]
    public void EveryDeclaredEvent_HasBothAttributesSoItsTopologyCanBeBuilt()
    {
        // The bus builds topology by scanning for [EventContract]. An event that carries one attribute
        // and not the other would be skipped or would throw at startup, so the whole set is checked
        // here rather than one event at a time.
        var events = typeof(IDomainEvent).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && type.IsAssignableTo(typeof(IDomainEvent)))
            .ToArray();

        events.ShouldNotBeEmpty();

        foreach (var declared in events)
        {
            Should.NotThrow(() => EventTypeName.Of(declared), $"{declared.Name} must declare its wire name");
        }
    }

    [BusEndpoint("factory-model", "cache-updater")]
    private sealed class CacheUpdaterProbe : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;
    }

    private sealed class UndeclaredProbe : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;
    }
}
