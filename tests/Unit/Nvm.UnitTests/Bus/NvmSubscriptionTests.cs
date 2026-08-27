using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.UnitTests.Bus;

public sealed class NvmSubscriptionTests
{
    [Fact]
    public void Of_BindsToTheContextExchangeAndTheEventsOwnRoutingKey()
    {
        // The publisher sends to nvm.factory-model with routing key
        // nvm.NV1.factory-model.revision-activated.v1. Both halves come from the same place, so they
        // cannot drift apart into a queue that exists, is bound, and stays empty.
        var subscriptions = NvmSubscription.Of(typeof(CacheConsumer));

        subscriptions.ShouldHaveSingleItem();
        subscriptions[0].Exchange.ShouldBe("nvm.factory-model");
        subscriptions[0].RoutingKey.ShouldBe("nvm.*.factory-model.revision-activated.v1");
    }

    [Fact]
    public void Of_UsesTheSingleSegmentWildcardSoOneSubscriptionMeansOneEvent()
    {
        // '#' would also match, and would also deliver every other event in the system to a consumer
        // that asked for one — including events from bounded contexts it has no business reading.
        NvmSubscription.Of(typeof(CacheConsumer))[0].RoutingKey.ShouldNotContain("#");
    }

    [Fact]
    public void Of_GivesTwoConsumersOfOneEventTheSameSubscription()
    {
        // Fan-out is not a property of the binding: both consumers ask for exactly the same messages.
        // What separates them is that each has its own queue — see NvmEndpointNameFormatter.
        NvmSubscription.Of(typeof(AuditConsumer))
            .ShouldBe(NvmSubscription.Of(typeof(CacheConsumer)));
    }

    [Fact]
    public void Of_DerivesOneSubscriptionPerEventTheConsumerHandles()
    {
        // Read off the IConsumer<T> interfaces rather than declared a second time. A consumer that
        // picks up another event gets its binding from the same edit that added the interface.
        NvmSubscription.Of(typeof(TwoEventConsumer)).Count.ShouldBe(1);
    }

    [Fact]
    public void Of_RefusesAConsumerThatHandlesNothingDeclaredAsAnEventContract()
    {
        // Refused at startup, loudly. Silently binding nothing would produce a service that runs, is
        // green in every dashboard, and processes not one message.
        var refusal = Should.Throw<InvalidOperationException>(
            () => NvmSubscription.Of(typeof(UndeclaredMessageConsumer)));

        refusal.Message.ShouldContain(nameof(UndeclaredMessageConsumer));
        refusal.Message.ShouldContain("EventContract");
    }

    private sealed class CacheConsumer : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;
    }

    private sealed class AuditConsumer : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;
    }

    // Handles a declared event and a plain message. Only the declared one belongs on this bus.
    private sealed class TwoEventConsumer
        : IConsumer<FactoryModelRevisionActivated>, IConsumer<NotAnEvent>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;

        public Task Consume(ConsumeContext<NotAnEvent> context) => Task.CompletedTask;
    }

    private sealed class UndeclaredMessageConsumer : IConsumer<NotAnEvent>
    {
        public Task Consume(ConsumeContext<NotAnEvent> context) => Task.CompletedTask;
    }

    private sealed record NotAnEvent(string Anything);
}
