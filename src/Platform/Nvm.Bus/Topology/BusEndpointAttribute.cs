namespace Nvm.Bus.Topology;

/// <summary>Declares the queue a consumer reads from: which context it belongs to, and its role.</summary>
/// <param name="context">The bounded context, for example <c>factory-model</c>.</param>
/// <param name="role">What this consumer does, in kebab-case, for example <c>cache-updater</c>.</param>
/// <remarks>
/// <para>
/// MassTransit will happily name a queue after the consumer class. That is convenient for about a
/// week, and then somebody renames <c>FactoryModelCacheConsumer</c> to
/// <c>EquipmentPathCacheConsumer</c> in the IDE. The rename is correct, the build is green, and on
/// the next deploy the service starts reading from a new empty queue while the old one sits in the
/// broker holding messages nobody will ever process.
/// </para>
/// <para>
/// A queue name is an operational fact — it appears in dashboards, alerts and runbooks — so it is
/// stated here and changes only when somebody means to change it. A consumer without this attribute
/// is refused at startup rather than given a name by accident.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BusEndpointAttribute(string context, string role) : Attribute
{
    /// <summary>The bounded context this consumer belongs to.</summary>
    public string Context { get; } = context;

    /// <summary>What the consumer does.</summary>
    public string Role { get; } = role;
}
