using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nvm.Analyzers;

/// <summary>Requires <c>[EventVersion(n)]</c>, n at least 1, on every event. AGENTS.md K6.</summary>
/// <remarks>
/// <para>
/// <b>Why it has to be there from v1, when there is only one version.</b> The marker looks redundant
/// on the day it is written and is impossible to add on the day it is needed. By the time a second
/// shape exists, v1 payloads are already in an append-only store and already on the wire — and none
/// of them says which shape they are. An upcaster chain has nothing to branch on, so the only
/// remaining option is guessing from the fields present.
/// </para>
/// <para>
/// Version 0 is refused as well. It is what a default <c>int</c> looks like, so accepting it means
/// accepting the one value that indicates nobody chose.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Nvm003EventVersionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id, as it appears in build output and in .editorconfig.</summary>
    public const string DiagnosticId = "NVM003";

    private const string Category = "Nvm.Contracts";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Event without a schema version",
        messageFormat:
            "Event '{0}' {1}. Every event states [EventVersion(n)] with n at least 1 from its first "
            + "version, because once v1 payloads are in the append-only store there is nowhere left "
            + "to add the marker (AGENTS.md K6).",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The version ends the event type string and the routing key, and is stored beside every "
            + "row so a reader in 2036 knows which shape it is holding.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var domainEvent = start.Compilation.GetTypeByMetadataName(ContractSymbols.DomainEventInterface);
            var versionAttribute = start.Compilation.GetTypeByMetadataName(ContractSymbols.EventVersionAttribute);

            // Nothing to enforce in a compilation that has never heard of either type.
            if (domainEvent is null || versionAttribute is null)
            {
                return;
            }

            start.RegisterSymbolAction(
                symbol => Analyze((INamedTypeSymbol)symbol.Symbol, symbol, domainEvent, versionAttribute),
                SymbolKind.NamedType);
        });
    }

    private static void Analyze(
        INamedTypeSymbol type,
        SymbolAnalysisContext context,
        INamedTypeSymbol domainEvent,
        INamedTypeSymbol versionAttribute)
    {
        if (!ContractSymbols.IsConcreteDomainEvent(type, domainEvent))
        {
            return;
        }

        // Not inherited, deliberately: EventVersionAttribute is declared Inherited = false, because a
        // derived event is a different type with a different wire name and must state its own number.
        var declared = type.GetAttributes().FirstOrDefault(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, versionAttribute));

        var fault = declared switch
        {
            null => "has no [EventVersion]",
            { ConstructorArguments.Length: > 0 } attribute when attribute.ConstructorArguments[0].Value is int version && version < 1 =>
                $"declares version {version}",
            _ => null,
        };

        if (fault is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            // The attribute's own location when there is one, so the fix is where the cursor lands.
            declared?.ApplicationSyntaxReference is { } reference
                ? Location.Create(reference.SyntaxTree, reference.Span)
                : type.Locations[0],
            type.Name,
            fault));
    }
}
