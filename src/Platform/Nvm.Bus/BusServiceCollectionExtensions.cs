using System.Reflection;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nvm.Bus.CloudEvents;
using Nvm.Bus.Topology;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Hosting;
using RabbitMQ.Client;

namespace Nvm.Bus;

/// <summary>Wires the Manufacturing Service Bus into a host.</summary>
public static class BusServiceCollectionExtensions
{
    /// <summary>The name this process's bus reports under on <c>/health/ready</c>.</summary>
    /// <remarks>
    /// <c>bus</c>, not <c>masstransit-bus</c>. Every other probe in this system is named after what it
    /// checks — <c>sqlserver</c>, <c>postgres</c>, <c>rabbitmq</c> — and this one checks the bus, not
    /// the library that implements it. Whoever reads a red line at 3 a.m. needs to know which
    /// dependency is unhappy, and would then look for a second, differently named probe for RabbitMQ
    /// — which is exactly the right thing to look for, because it exists and means something else.
    /// </remarks>
    public const string HealthCheckName = "bus";

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

            ConfigureHealthCheck(bus);

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

    /// <summary>States the bus health check's name and tags instead of inheriting them.</summary>
    /// <remarks>
    /// <para>
    /// <c>AddMassTransit</c> registers a health check on its own, and left alone it appears as
    /// <c>masstransit-bus</c> with whatever tags that version of the library happens to choose. Both
    /// are operational facts — the name shows up in dashboards and runbooks, and the tag decides which
    /// endpoint the probe answers on — so both are stated here, for the same reason a queue name is
    /// stated in <see cref="Topology.BusEndpointAttribute"/> rather than derived.
    /// </para>
    /// <para>
    /// The tag is <b>ready only, never live</b>. This check fails when the bus cannot serve, and a
    /// process whose bus cannot serve is still a process that must not be restarted — putting it on
    /// liveness turns a broker outage into a restart loop across every instance at once, which is
    /// exactly what N15 forbids.
    /// </para>
    /// <para>
    /// <b>What it does and does not tell you.</b> It reports on <i>this process's</i> bus: whether it
    /// started, and whether its receive endpoints are ready. It is not a broker probe. A host that
    /// only publishes has no receive endpoints, so after a bus has started successfully this check
    /// stays healthy even while the broker is gone — measured in M1/C14. Reachability of the broker is
    /// answered by the separate <c>rabbitmq</c> probe in the host, and the two are not
    /// interchangeable.
    /// </para>
    /// </remarks>
    private static void ConfigureHealthCheck(IBusRegistrationConfigurator bus) =>
        bus.ConfigureHealthCheckOptions(health =>
        {
            health.Name = HealthCheckName;

            // Cleared, not added to. The defaults are whatever the library chose, and appending would
            // leave this check answering on an endpoint nobody here decided it should answer on.
            health.Tags.Clear();
            health.Tags.Add(HealthTags.Ready);

            // Unhealthy, not the Degraded that MassTransit would otherwise report while the bus is
            // still coming up. Degraded answers HTTP 200, so an instance whose bus cannot carry a
            // message would stay in rotation — a readiness probe that never goes red checks nothing.
            // (MassTransit 8.5 marked FailureStatus obsolete; this one property now sets both.)
            health.MinimalFailureStatus = HealthStatus.Unhealthy;
        });

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
