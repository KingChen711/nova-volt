using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nvm.Analyzers;

/// <summary>Refuses any read of the machine clock. AGENTS.md K1.</summary>
/// <remarks>
/// <para>
/// <b>Why this is a build error and not a code review note.</b> Formation runs for hours and aging
/// for weeks. A saga that waits twelve days can only be tested by moving the clock, and a clock that
/// is read from a static property cannot be moved — the test would have to wait twelve days, so it is
/// never written, and the twelve-day path ships unexercised.
/// </para>
/// <para>
/// <c>TimeProvider</c> is the seam. Injected, it is <c>TimeProvider.System</c> in production and
/// <c>FakeTimeProvider</c> in a test that skips a fortnight in a millisecond.
/// </para>
/// <para>
/// Matching is done on the <b>symbol</b>, not on the text. A syntax check for the string
/// <c>"DateTime.UtcNow"</c> is defeated by <c>using Clock = System.DateTime;</c> or by a fully
/// qualified name, and an analyzer that is trivially avoidable teaches people to avoid it.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Nvm001DateTimeNowAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id, as it appears in build output and in .editorconfig.</summary>
    public const string DiagnosticId = "NVM001";

    private const string Category = "Nvm.Determinism";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Reading the machine clock is forbidden",
        // RS1032: either one sentence with no full stop, or several with one. The message has to say
        // what to do instead — a diagnostic that only says "forbidden" gets suppressed, not fixed.
        messageFormat:
            "'{0}.{1}' reads the machine clock. Inject TimeProvider and call GetUtcNow() instead, "
            + "so that a saga spanning days stays testable in milliseconds (AGENTS.md K1).",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Time must arrive through TimeProvider so that FakeTimeProvider can move it. Formation "
            + "and aging span days; a path that can only be reached by waiting is a path nobody tests.");

    // Members that hand back "now" without being asked where now comes from. DateTimeOffset has no
    // Today, so the pairs are not symmetric — they are listed rather than inferred for that reason.
    private static readonly ImmutableHashSet<string> BannedOnDateTime =
        ImmutableHashSet.Create("Now", "UtcNow", "Today");

    private static readonly ImmutableHashSet<string> BannedOnDateTimeOffset =
        ImmutableHashSet.Create("Now", "UtcNow");

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

        // Generated code is exempt. Nobody can fix a violation in a file that is rewritten on every
        // build, and the only outcome would be people switching the whole rule off.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        // Types resolved once per compilation rather than once per property reference. The alternative
        // compares full type names as strings on every node in the codebase.
        context.RegisterCompilationStartAction(compilation =>
        {
            var dateTime = compilation.Compilation.GetSpecialType(SpecialType.System_DateTime);
            var dateTimeOffset = compilation.Compilation.GetTypeByMetadataName("System.DateTimeOffset");

            var banned = new Dictionary<ISymbol, ImmutableHashSet<string>>(SymbolEqualityComparer.Default)
            {
                [dateTime] = BannedOnDateTime,
            };

            if (dateTimeOffset is not null)
            {
                banned[dateTimeOffset] = BannedOnDateTimeOffset;
            }

            compilation.RegisterOperationAction(operation => Analyze(operation, banned), OperationKind.PropertyReference);
        });
    }

    private static void Analyze(
        OperationAnalysisContext context,
        Dictionary<ISymbol, ImmutableHashSet<string>> banned)
    {
        var property = ((IPropertyReferenceOperation)context.Operation).Property;

        if (property.ContainingType is null
            || !banned.TryGetValue(property.ContainingType, out var members)
            || !members.Contains(property.Name))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            context.Operation.Syntax.GetLocation(),
            property.ContainingType.Name,
            property.Name));
    }
}
