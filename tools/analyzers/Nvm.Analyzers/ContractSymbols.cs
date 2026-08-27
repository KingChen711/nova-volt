using Microsoft.CodeAnalysis;

namespace Nvm.Analyzers;

/// <summary>The few names from <c>Nvm.Contracts</c> that the analyzers have to recognise.</summary>
/// <remarks>
/// <para>
/// An analyzer cannot reference the assembly it analyses — it is loaded by the compiler, and the
/// compiler is building that assembly. So the connection is by name, resolved per compilation.
/// </para>
/// <para>
/// That makes these strings a real coupling: renaming <c>IDomainEvent</c> or moving it to another
/// namespace turns both rules off, and the symptom is a green build. The architecture test in C17 is
/// what notices, because it asserts the type exists where these strings say it does.
/// </para>
/// </remarks>
internal static class ContractSymbols
{
    internal const string DomainEventInterface = "Nvm.Contracts.Events.IDomainEvent";

    internal const string EventVersionAttribute = "Nvm.Contracts.Events.EventVersionAttribute";

    /// <summary>The assembly where every wire contract lives.</summary>
    /// <remarks>
    /// NVM002 applies to this whole assembly, not only to events. A record that is not an event today
    /// becomes the payload of one tomorrow, and by then the <c>DateTime</c> inside it is already
    /// serialized into the store.
    /// </remarks>
    internal const string ContractsAssembly = "Nvm.Contracts";

    /// <summary>Whether a type is a concrete event, as opposed to the marker or an abstract base.</summary>
    internal static bool IsConcreteDomainEvent(INamedTypeSymbol type, INamedTypeSymbol? domainEvent) =>
        domainEvent is not null
        && type is { IsAbstract: false, TypeKind: TypeKind.Class or TypeKind.Struct }
        && type.AllInterfaces.Contains(domainEvent, SymbolEqualityComparer.Default);

    /// <summary>Whether a type belongs to the wire contract surface.</summary>
    /// <remarks>
    /// Three tests, ORed, because each catches what the others miss: a type in another assembly that
    /// implements the marker; a type inside the contracts project put in some other namespace; and a
    /// contract-shaped type that has not been made an event yet. The last is the common one — a
    /// record that is not an event today becomes the payload of one next milestone.
    /// </remarks>
    internal static bool IsWireContract(INamedTypeSymbol type, INamedTypeSymbol? domainEvent, bool assemblyIsContracts) =>
        IsConcreteDomainEvent(type, domainEvent)
        || assemblyIsContracts
        || IsInContractsNamespace(type);

    private static bool IsInContractsNamespace(INamedTypeSymbol type)
    {
        var name = type.ContainingNamespace?.ToDisplayString();

        return name is not null
            && (string.Equals(name, ContractsAssembly, StringComparison.Ordinal)
                || name.StartsWith(ContractsAssembly + ".", StringComparison.Ordinal));
    }
}
