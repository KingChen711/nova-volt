using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nvm.Analyzers;

/// <summary>Refuses <c>DateTime</c> anywhere in a wire contract. AGENTS.md K2.</summary>
/// <remarks>
/// <para>
/// <b>What goes wrong, and where.</b> <c>DateTime</c> carries a <c>Kind</c> flag, not an offset, and
/// the flag does not survive JSON: serialize a local <c>DateTime</c> and read it back and you get the
/// same wall-clock reading with no way to know what it meant. Site DE1 is Leipzig and observes
/// daylight saving, so one hour every autumn happens twice. A traceability record written in that
/// hour is ambiguous forever, and the event store is append-only — there is no correcting it later.
/// </para>
/// <para>
/// The failure is seasonal, which is why it needs a compiler and not a review. Everything works for
/// months, on every machine, in every test, and then one Sunday morning in October the ordering of
/// two events flips.
/// </para>
/// <para>
/// <b>Why the whole contract surface, not just events.</b> A record in <c>Nvm.Contracts</c> that is
/// not an event today becomes the payload of one tomorrow, and by then its <c>DateTime</c> is already
/// serialized into rows nobody may rewrite. See <see cref="ContractSymbols.IsWireContract"/> for the
/// three ways a type qualifies.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Nvm002DateTimeInContractAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id, as it appears in build output and in .editorconfig.</summary>
    public const string DiagnosticId = "NVM002";

    private const string Category = "Nvm.Contracts";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DateTime in a wire contract",
        messageFormat:
            "'{0}.{1}' is typed '{2}', which carries no UTC offset. Use DateTimeOffset, or site DE1 "
            + "records an ambiguous timestamp for one hour every autumn (AGENTS.md K2).",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "DateTime has a Kind flag that does not survive serialization. Events are append-only and "
            + "are read years later, so an ambiguous timestamp can never be corrected.");

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
            var dateTime = start.Compilation.GetSpecialType(SpecialType.System_DateTime);

            var wholeAssemblyIsContracts = string.Equals(
                start.Compilation.Assembly.Name,
                ContractSymbols.ContractsAssembly,
                StringComparison.Ordinal);

            start.RegisterSymbolAction(
                symbol => Analyze((INamedTypeSymbol)symbol.Symbol, symbol, domainEvent, dateTime, wholeAssemblyIsContracts),
                SymbolKind.NamedType);
        });
    }

    private static void Analyze(
        INamedTypeSymbol type,
        SymbolAnalysisContext context,
        INamedTypeSymbol? domainEvent,
        INamedTypeSymbol dateTime,
        bool wholeAssemblyIsContracts)
    {
        if (!ContractSymbols.IsWireContract(type, domainEvent, wholeAssemblyIsContracts))
        {
            return;
        }

        foreach (var member in type.GetMembers())
        {
            var memberType = member switch
            {
                // Positional record parameters arrive here as properties, which is why constructor
                // parameters are not inspected separately — doing both reports the same field twice.
                IPropertySymbol property => property.Type,

                // Backing fields are implicitly declared and would double every property above.
                IFieldSymbol { IsImplicitlyDeclared: false } field => field.Type,
                _ => null,
            };

            if (memberType is null || !Mentions(memberType, dateTime))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                member.Locations.FirstOrDefault() ?? type.Locations[0],
                type.Name,
                member.Name,
                memberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    /// <summary>Whether <c>DateTime</c> appears anywhere inside a type, however deeply nested.</summary>
    /// <remarks>
    /// The recursion is the point. <c>List&lt;DateTime&gt;</c>, <c>DateTime?</c> (which is
    /// <c>Nullable&lt;DateTime&gt;</c>), <c>DateTime[]</c> and
    /// <c>Dictionary&lt;string, List&lt;DateTime&gt;&gt;</c> all serialize the same broken value as a
    /// bare field does. A rule that only compares the outermost type is the version of this analyzer
    /// that everybody writes first, and it passes every test written against a plain property.
    /// </remarks>
    private static bool Mentions(ITypeSymbol type, INamedTypeSymbol dateTime)
    {
        if (SymbolEqualityComparer.Default.Equals(type, dateTime))
        {
            return true;
        }

        return type switch
        {
            IArrayTypeSymbol array => Mentions(array.ElementType, dateTime),
            INamedTypeSymbol named => named.TypeArguments.Any(argument => Mentions(argument, dateTime)),
            _ => false,
        };
    }
}
