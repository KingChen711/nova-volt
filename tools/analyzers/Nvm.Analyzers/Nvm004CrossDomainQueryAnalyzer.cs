using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nvm.Analyzers;

/// <summary>Cấm <c>Query&lt;T&gt;</c> đọc entity của domain khác trong handler, pre-check, post-action. AGENTS.md K8.</summary>
/// <remarks>
/// <para>
/// <c>Query</c> đọc trong transaction của command, mà transaction chỉ phủ một domain. Đọc entity master data
/// trong command vận hành phải đi qua <c>ProjectionQuery</c>: ngoài transaction, chỉ bản đã commit. Nền thật
/// chặn việc này lúc biên dịch model; Foundation chặn hai lần: rule này lúc build, và
/// <c>ModelMisuseException</c> lúc chạy cho đường gọi mà rule không thấy (generic, reflection).
/// </para>
/// <para>
/// Domain đọc từ attribute: <c>[Entity&lt;TBlock&gt;]</c>/<c>[Facet&lt;TBlock&gt;]</c> của kiểu được đọc và
/// <c>[Command&lt;TBlock&gt;]</c> của command mà lớp đang xử lý, rồi <c>[FunctionalBlock(name, domain)]</c> của block.
/// Thiếu một mắt xích thì im lặng: rule không đoán.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Nvm004CrossDomainQueryAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Id của diagnostic, đúng như nó xuất hiện trong build output và trong .editorconfig.</summary>
    public const string DiagnosticId = "NVM004";

    private const string Category = "Nvm.Model";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Query reads an entity of another domain",
        messageFormat:
            "Query<{0}> reads {1} data inside a {2} command ('{3}'). Query stays in the command's domain and "
            + "transaction; read other domains with ProjectionQuery (AGENTS.md K8).",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A command transaction covers one domain. Reading another domain inside it must use ProjectionQuery, "
            + "which reads committed data outside the transaction.");

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
            var symbols = Symbols.Resolve(start.Compilation);
            if (symbols is null)
            {
                return;
            }

            start.RegisterOperationAction(operation => Analyze((IInvocationOperation)operation.Operation, operation, symbols),
                OperationKind.Invocation);
        });
    }

    private static void Analyze(IInvocationOperation invocation, OperationAnalysisContext context, Symbols symbols)
    {
        var method = invocation.TargetMethod;
        if (method.Name != "Query" || method.TypeArguments.Length != 1
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, symbols.ReadOnlyPlatform))
        {
            return;
        }

        var target = method.TypeArguments[0];
        var targetDomain = DomainOf(target, symbols, symbols.EntityAttribute, symbols.FacetAttribute);
        var command = HandledCommand(context.ContainingSymbol.ContainingType, symbols);
        var commandDomain = command is null ? null : DomainOf(command, symbols, symbols.CommandAttribute);
        if (targetDomain is null || commandDomain is null || targetDomain == commandDomain)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), target.Name, targetDomain,
            commandDomain, command!.Name));
    }

    /// <summary>Command mà lớp chứa lời gọi đang xử lý: tham số đầu của handler, pre-check hoặc post-action.</summary>
    private static ITypeSymbol? HandledCommand(INamedTypeSymbol? type, Symbols symbols)
    {
        if (type is null)
        {
            return null;
        }

        foreach (var contract in type.AllInterfaces)
        {
            var definition = contract.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(definition, symbols.CommandHandler)
                || SymbolEqualityComparer.Default.Equals(definition, symbols.PreCheck)
                || SymbolEqualityComparer.Default.Equals(definition, symbols.PostAction))
            {
                return contract.TypeArguments[0];
            }
        }

        return null;
    }

    private static string? DomainOf(ITypeSymbol type, Symbols symbols, params INamedTypeSymbol?[] markers)
    {
        foreach (var attribute in type.GetAttributes())
        {
            var definition = attribute.AttributeClass?.OriginalDefinition;
            if (definition is null || !markers.Any(m => SymbolEqualityComparer.Default.Equals(definition, m)))
            {
                continue;
            }

            var block = attribute.AttributeClass!.TypeArguments[0];
            var declared = block.GetAttributes().FirstOrDefault(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.FunctionalBlockAttribute));
            if (declared is { ConstructorArguments.Length: 2 })
            {
                // Giá trị enum đến dưới dạng số; tên lấy từ field có cùng hằng số trong ModelDomain.
                var domain = declared.ConstructorArguments[1];
                return domain.Type?.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, domain.Value))?.Name;
            }
        }

        return null;
    }

    private sealed class Symbols
    {
        public INamedTypeSymbol? ReadOnlyPlatform { get; private set; }

        public INamedTypeSymbol? FunctionalBlockAttribute { get; private set; }

        public INamedTypeSymbol? EntityAttribute { get; private set; }

        public INamedTypeSymbol? FacetAttribute { get; private set; }

        public INamedTypeSymbol? CommandAttribute { get; private set; }

        public INamedTypeSymbol? CommandHandler { get; private set; }

        public INamedTypeSymbol? PreCheck { get; private set; }

        public INamedTypeSymbol? PostAction { get; private set; }

        public static Symbols? Resolve(Compilation compilation)
        {
            var symbols = new Symbols
            {
                ReadOnlyPlatform = compilation.GetTypeByMetadataName(ContractSymbols.ReadOnlyPlatform),
                FunctionalBlockAttribute = compilation.GetTypeByMetadataName(ContractSymbols.FunctionalBlockAttribute),
                EntityAttribute = compilation.GetTypeByMetadataName(ContractSymbols.EntityAttribute),
                FacetAttribute = compilation.GetTypeByMetadataName(ContractSymbols.FacetAttribute),
                CommandAttribute = compilation.GetTypeByMetadataName(ContractSymbols.CommandAttribute),
                CommandHandler = compilation.GetTypeByMetadataName(ContractSymbols.CommandHandler),
                PreCheck = compilation.GetTypeByMetadataName(ContractSymbols.PreCheck),
                PostAction = compilation.GetTypeByMetadataName(ContractSymbols.PostAction),
            };

            // Compilation không thấy Foundation thì không có gì để kiểm.
            return symbols.ReadOnlyPlatform is null || symbols.FunctionalBlockAttribute is null ? null : symbols;
        }
    }
}
