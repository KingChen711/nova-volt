using MassTransit;
using RabbitMQ.Client;

namespace Nvm.Bus.Topology;

/// <summary>
/// Gives a consumer its own queue, bound to the context exchange by the routing key of every event
/// it consumes.
/// </summary>
/// <typeparam name="TConsumer">The consumer being placed on the bus.</typeparam>
/// <remarks>
/// <para>
/// This is the half of the topology that MassTransit cannot infer. The publish half — one topic
/// exchange per bounded context, routing key <c>nvm.{site}.{context}.{event}.v{n}</c> — is set in
/// <see cref="BusServiceCollectionExtensions"/>. On a <b>topic</b> exchange nothing is delivered
/// until somebody states a pattern to match, and MassTransit's default binding carries an empty
/// routing key, which matches nothing at all. A consumer wired up without this definition therefore
/// starts cleanly, appears in the management UI, and receives nothing forever.
/// </para>
/// <para>
/// What it subscribes to is worked out by <see cref="NvmSubscription"/> and never assembled here by
/// joining strings. AMQP compares routing keys byte for byte, so <c>nvm.nv1.#</c> and <c>nvm.NV1.#</c>
/// are two different subscriptions and the broker reports no error for either — the only defence is
/// that one piece of code builds both sides.
/// </para>
/// </remarks>
public sealed class NvmConsumerDefinition<TConsumer> : ConsumerDefinition<TConsumer>
    where TConsumer : class, IConsumer
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The consumer handles nothing that is a declared event contract, so there is no routing key to
    /// bind it by.
    /// </exception>
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(endpointConfigurator);

        // Asked for unconditionally, so a consumer with nothing to subscribe to is refused at startup
        // even on a transport that has no exchanges to bind.
        var subscriptions = NvmSubscription.Of(typeof(TConsumer));

        // The in-memory transport used by test harnesses has no exchanges, and asking it to bind one
        // would fail. Skipping keeps the same definition usable in both places, which is the point: a
        // test that configured its own bindings would be testing its own copy of the topology.
        if (endpointConfigurator is not IRabbitMqReceiveEndpointConfigurator rabbit)
        {
            return;
        }

        // MassTransit would otherwise declare the context exchange for itself, with its own default
        // exchange type and an empty routing key. Two problems in one: the declaration disagrees with
        // the publisher's `topic` and RabbitMQ answers PRECONDITION_FAILED, and the binding that
        // survives matches no message this system sends.
        rabbit.ConfigureConsumeTopology = false;

        foreach (var subscription in subscriptions)
        {
            rabbit.Bind(subscription.Exchange, binding =>
            {
                // Must match how the publisher declares it, byte for byte — RabbitMQ refuses to
                // redeclare an existing exchange with different properties, and the failure arrives at
                // startup as a channel-level error rather than as anything about topology.
                binding.ExchangeType = ExchangeType.Topic;
                binding.RoutingKey = subscription.RoutingKey;
            });
        }
    }
}
