using System.Reflection;
using MassTransit;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;

namespace Nvm.Bus.Topology;

/// <summary>One thing a consumer has asked for: an exchange, and a pattern to match on it.</summary>
/// <param name="Exchange">The context exchange, for example <c>nvm.factory-model</c>.</param>
/// <param name="RoutingKey">The binding pattern, for example
/// <c>nvm.*.factory-model.revision-activated.v1</c>.</param>
/// <remarks>
/// Separated from <see cref="NvmConsumerDefinition{TConsumer}"/> so that what a consumer subscribes to
/// can be asserted without a broker. Binding is the half of a topology that fails silently — a wrong
/// pattern produces a queue that exists, is bound, reports no error, and stays empty — so it is the
/// half that most needs a test that can be red.
/// </remarks>
public sealed record NvmSubscription(string Exchange, string RoutingKey)
{
    /// <summary>Works out what a consumer subscribes to, from the events it handles.</summary>
    /// <param name="consumerType">The consumer class.</param>
    /// <exception cref="InvalidOperationException">
    /// The consumer handles nothing that carries <see cref="EventContractAttribute"/>, so there is no
    /// routing key to bind it by.
    /// </exception>
    /// <remarks>
    /// Derived from the <c>IConsumer&lt;T&gt;</c> interfaces rather than declared a second time next to
    /// them. A consumer that starts handling a second event gets its binding from the same edit that
    /// added the interface, and cannot end up subscribed to something it no longer handles.
    /// </remarks>
    public static IReadOnlyList<NvmSubscription> Of(Type consumerType)
    {
        ArgumentNullException.ThrowIfNull(consumerType);

        var subscriptions = ConsumedEventTypes(consumerType)
            .Select(EventTypeName.Of)
            .Select(eventType => new NvmSubscription(
                NvmTopology.ExchangeFor(eventType),

                // Every site, not one. A probe or a projection wants the fact wherever it happened; a
                // service that runs inside one plant asks for NvmTopology.BindingForEvent instead, and
                // that choice is the whole of multiplant isolation on the consume side.
                NvmTopology.BindingForEventAtEverySite(eventType)))
            .ToArray();

        return subscriptions.Length > 0
            ? subscriptions
            : throw new InvalidOperationException(
                $"Consumer '{consumerType.Name}' handles no event carrying [EventContract], so no "
                + "binding can be derived for it. Either it consumes a message that is not a domain "
                + "event — which does not belong on this bus — or the contract is missing its attribute.");
    }

    private static IEnumerable<Type> ConsumedEventTypes(Type consumerType) =>
        consumerType
            .GetInterfaces()
            .Where(contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .Select(contract => contract.GetGenericArguments()[0])
            .Where(message => message.IsAssignableTo(typeof(IDomainEvent))
                && message.GetCustomAttribute<EventContractAttribute>() is not null);
}
