namespace Nvm.Contracts.Events;

/// <summary>Declares the wire name of a domain event: its bounded context and its event name.</summary>
/// <param name="context">The bounded context that owns the event, for example <c>factory-model</c>.</param>
/// <param name="name">The event in kebab-case, for example <c>revision-activated</c>.</param>
/// <remarks>
/// <para>
/// Stated rather than derived. The obvious alternative is to build the wire name out of the C# type
/// and namespace — <c>Nvm.Contracts.Events.FactoryModel.FactoryModelRevisionActivated</c> could be
/// folded into the same string automatically. That would make a rename in the IDE into a change of
/// contract: the exchange moves, existing bindings stop matching, and nothing warns anybody. The
/// wire name is an operational fact and belongs in the source as one.
/// </para>
/// <para>
/// Together with <see cref="EventVersionAttribute"/> this is everything an
/// <see cref="CloudEvents.EventTypeName"/> needs. The two are separate attributes because they change
/// for different reasons: the version moves when the schema does, the name never moves at all.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventContractAttribute(string context, string name) : Attribute
{
    /// <summary>The bounded context that owns the event.</summary>
    public string Context { get; } = context;

    /// <summary>The event name, in kebab-case.</summary>
    public string Name { get; } = name;
}
