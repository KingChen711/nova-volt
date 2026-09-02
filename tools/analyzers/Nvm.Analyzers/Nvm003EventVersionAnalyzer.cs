using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nvm.Analyzers;

/// <summary>Yêu cầu <c>[EventVersion(n)]</c>, n ít nhất là 1, trên mọi event. AGENTS.md K6.</summary>
/// <remarks>
/// <para>
/// <b>Vì sao nó phải có mặt từ v1, khi chỉ có một version.</b> Cái marker trông thừa thãi vào ngày nó
/// được viết ra và không thể thêm vào được vào ngày nó thực sự cần. Đến lúc một hình dạng thứ hai xuất
/// hiện, payload v1 đã nằm trong append-only store và đã ở trên wire rồi — và không cái nào trong số
/// đó nói nó là hình dạng nào. Một chuỗi upcaster không có gì để rẽ nhánh, nên lựa chọn duy nhất còn
/// lại là đoán dựa trên các field đang có mặt.
/// </para>
/// <para>
/// Version 0 cũng bị từ chối. Đó là hình dạng của một <c>int</c> mặc định, nên chấp nhận nó có nghĩa
/// là chấp nhận giá trị duy nhất cho biết không ai đã chọn cả.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Nvm003EventVersionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Id của diagnostic, đúng như nó xuất hiện trong build output và trong .editorconfig.</summary>
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

            // Không có gì để enforce trong một compilation chưa từng nghe đến type nào trong hai type đó.
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

        // Không kế thừa, có chủ đích: EventVersionAttribute được khai báo Inherited = false, vì một
        // event dẫn xuất là một type khác với một wire name khác và phải tự nêu ra con số của riêng nó.
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
            // Vị trí của chính attribute khi nó tồn tại, để chỗ sửa cũng là nơi con trỏ dừng lại.
            declared?.ApplicationSyntaxReference is { } reference
                ? Location.Create(reference.SyntaxTree, reference.Span)
                : type.Locations[0],
            type.Name,
            fault));
    }
}
