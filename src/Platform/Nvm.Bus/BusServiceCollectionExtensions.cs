using System.Reflection;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Nvm.Bus.CloudEvents;
using Nvm.Bus.Topology;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using RabbitMQ.Client;

namespace Nvm.Bus;

/// <summary>Wires the Manufacturing Service Bus into a host.</summary>
public static class BusServiceCollectionExtensions
{
    /// <summary>Registers MassTransit against RabbitMQ with this system's topology.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="configureOptions">Connection settings.</param>
    /// <param name="registerConsumers">
    /// Where a host adds its consumers. Left empty by a host that only publishes.
    /// </param>
    /// <exception cref="InvalidOperationException">The options cannot describe a reachable broker.</exception>
    public static IServiceCollection AddNvmBus(
        this IServiceCollection services,
        Action<NvmBusOptions> configureOptions,
        Action<IBusRegistrationConfigurator>? registerConsumers = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new NvmBusOptions();
        configureOptions(options);
        options.Validate();

        services.AddMassTransit(bus =>
        {
            // Queue names come from what a consumer is for, not from what its class is called.
            bus.SetEndpointNameFormatter(NvmEndpointNameFormatter.Instance);

            registerConsumers?.Invoke(bus);

            // Applied to every receive endpoint, including ones a Functional Block adds later. Put on
            // one endpoint at a time, this is the sort of thing that gets copied four times and
            // forgotten on the fifth.
            bus.AddConfigureEndpointsCallback((_, _, endpoint) =>
            {
                endpoint.UseMessageRetry(retry => retry.Intervals(NvmRetryPolicy.Intervals(Random.Shared)));

                // RabbitMQ 4 removed classic queue mirroring; quorum queues are what replaced it. On a
                // single development node both kinds behave identically, so choosing wrong here stays
                // invisible until a second node exists — and by then it cannot be corrected in place.
                // Changing a queue's type means deleting it, along with whatever is still inside.
                if (endpoint is IRabbitMqReceiveEndpointConfigurator rabbit)
                {
                    rabbit.SetQuorumQueue();
                }
            });

            bus.UsingRabbitMq((context, configurator) =>
            {
                configurator.Host(options.Host, options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });

                // Topology first, and the order is not cosmetic. MassTransit locks a message's entity
                // name the moment anything reads it, and installing the publish and send filters reads
                // it — so calling UseNvmCloudEvents first makes the SetEntityName below throw
                // "entity name was already evaluated" and the process never starts.
                ApplyEventTopology(configurator);
                configurator.UseNvmCloudEvents(options.ApplicationName);

                configurator.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    /// <summary>Registers a consumer together with the queue and bindings this system gives it.</summary>
    /// <typeparam name="TConsumer">The consumer to place on the bus.</typeparam>
    /// <param name="bus">The registration being built inside <see cref="AddNvmBus"/>.</param>
    /// <remarks>
    /// The only sanctioned way to add a consumer. Plain <c>AddConsumer&lt;T&gt;</c> also compiles and
    /// also starts, and produces a queue bound to the context exchange with an empty routing key —
    /// which on a topic exchange means the consumer receives nothing, with no error anywhere to say
    /// so. See <see cref="Topology.NvmConsumerDefinition{TConsumer}"/>.
    /// </remarks>
    public static IBusRegistrationConfigurator AddNvmConsumer<TConsumer>(this IBusRegistrationConfigurator bus)
        where TConsumer : class, IConsumer
    {
        ArgumentNullException.ThrowIfNull(bus);

        bus.AddConsumer<TConsumer, NvmConsumerDefinition<TConsumer>>();

        return bus;
    }

    /// <summary>
    /// Points every declared event at its context's topic exchange, with the routing key this system
    /// uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MassTransit's default is one exchange per message type, named after the .NET type. That would
    /// make a namespace rename into a topology change, and it gives consumers no way to say "anything
    /// happening at Hai Phong" — a fan-out exchange per type has no routing key to match on.
    /// </para>
    /// <para>
    /// So every event carrying <c>[EventContract]</c> is redirected: exchange
    /// <c>nvm.{context}</c>, type <c>topic</c>, routing key
    /// <c>nvm.{site}.{context}.{event}.v{n}</c>. Discovered by scanning the contracts assembly rather
    /// than listed here, so adding an event is one attribute and not two edits in two projects.
    /// </para>
    /// </remarks>
    private static void ApplyEventTopology(IRabbitMqBusFactoryConfigurator configurator)
    {
        var apply = typeof(BusServiceCollectionExtensions)
            .GetMethod(nameof(ApplyTopologyFor), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var eventType in DeclaredEventTypes.All())
        {
            apply.MakeGenericMethod(eventType).Invoke(null, [configurator]);
        }
    }

    private static void ApplyTopologyFor<TEvent>(IRabbitMqBusFactoryConfigurator configurator)
        where TEvent : class, IDomainEvent
    {
        var eventType = EventTypeName.Of(typeof(TEvent));
        var exchange = NvmTopology.ExchangeFor(eventType);

        configurator.Message<TEvent>(message => message.SetEntityName(exchange));

        // Topic, not the fanout MassTransit would pick. Fanout has no routing key, so every consumer
        // bound to the exchange receives every message on it and filters in code — which is exactly
        // how a Leipzig message ends up inside a Hai Phong process.
        configurator.Publish<TEvent>(publish => publish.ExchangeType = ExchangeType.Topic);

        configurator.Send<TEvent>(send => send.UseRoutingKeyFormatter(
            sendContext => RoutingKey.Create(sendContext.Message.SiteId, eventType).Value));
    }
}
