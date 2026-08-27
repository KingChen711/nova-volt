using System.Reflection;
using Nvm.Contracts.Events;

namespace Nvm.Bus;

/// <summary>The events this system knows how to put on the bus.</summary>
/// <remarks>
/// Discovered by scanning the contracts assembly for <see cref="EventContractAttribute"/> rather than
/// listed anywhere. Adding an event is then one attribute on the record, not that plus two edits in
/// a project the author has no reason to open — and a list maintained by hand is a list that goes
/// stale on the event nobody remembered.
/// </remarks>
internal static class DeclaredEventTypes
{
    internal static IEnumerable<Type> All() =>
        typeof(IDomainEvent).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && type.IsAssignableTo(typeof(IDomainEvent))
                && type.GetCustomAttribute<EventContractAttribute>() is not null);
}
