using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nvm.Analyzers;

/// <summary>Từ chối <c>DateTime</c> ở bất kỳ đâu trong event, entity hoặc facet. AGENTS.md K2.</summary>
/// <remarks>
/// <para>
/// <b>Cái gì sai, và sai ở đâu.</b> <c>DateTime</c> mang theo một cờ <c>Kind</c>, không phải một
/// offset, và cờ đó không sống sót qua JSON hay cột SQL: ghi một <c>DateTime</c> kiểu local rồi đọc lại
/// và bạn nhận được đúng giá trị wall-clock đó mà không có cách nào biết nó từng có nghĩa gì. Khung ca
/// tính theo múi giờ của plant; ở plant có daylight saving, một giờ mỗi mùa thu xảy ra hai lần. Một
/// bản ghi truy vết trong giờ đó sẽ mập mờ mãi mãi.
/// </para>
/// <para>
/// Lỗi này mang tính mùa vụ, đó là lý do nó cần một compiler chứ không phải một review. Mọi thứ hoạt
/// động tốt hàng tháng trời, trên mọi máy, trong mọi test, rồi một sáng Chủ Nhật tháng Mười thứ tự
/// của hai event bỗng đảo ngược.
/// </para>
/// <para>
/// <b>Áp cho event và cho entity/facet.</b> Event lên bus và vào bảng event; entity và facet là cột
/// của DB MOM. Cả hai được đọc lại nhiều năm sau và không ai được ghi lại chúng. Xem
/// <see cref="ContractSymbols.IsPersistedContract"/>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Nvm002DateTimeInContractAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Id của diagnostic, đúng như nó xuất hiện trong build output và trong .editorconfig.</summary>
    public const string DiagnosticId = "NVM002";

    private const string Category = "Nvm.Contracts";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DateTime in an event or a persisted model object",
        messageFormat:
            "'{0}.{1}' is typed '{2}', which carries no UTC offset. Use DateTimeOffset: shift windows "
            + "follow the plant time zone, and a DST hour happens twice every autumn (AGENTS.md K2).",
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
            var foundationEvent = start.Compilation.GetTypeByMetadataName(ContractSymbols.FoundationEvent);
            var modelObject = start.Compilation.GetTypeByMetadataName(ContractSymbols.ModelObject);
            var dateTime = start.Compilation.GetSpecialType(SpecialType.System_DateTime);

            start.RegisterSymbolAction(
                symbol => Analyze((INamedTypeSymbol)symbol.Symbol, symbol, foundationEvent, modelObject, dateTime),
                SymbolKind.NamedType);
        });
    }

    private static void Analyze(
        INamedTypeSymbol type,
        SymbolAnalysisContext context,
        INamedTypeSymbol? foundationEvent,
        INamedTypeSymbol? modelObject,
        INamedTypeSymbol dateTime)
    {
        if (!ContractSymbols.IsPersistedContract(type, foundationEvent, modelObject))
        {
            return;
        }

        foreach (var member in type.GetMembers())
        {
            var memberType = member switch
            {
                // Positional record parameter đến đây dưới dạng property, đó là lý do constructor
                // parameter không được kiểm tra riêng — làm cả hai sẽ báo cáo cùng một field hai lần.
                IPropertySymbol property => property.Type,

                // Backing field được khai báo ngầm định và sẽ nhân đôi mọi property ở trên.
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

    /// <summary><c>DateTime</c> có xuất hiện ở đâu đó bên trong một type hay không, dù lồng sâu đến đâu.</summary>
    /// <remarks>
    /// Phép đệ quy chính là điểm mấu chốt. <c>List&lt;DateTime&gt;</c>, <c>DateTime?</c> (tức là
    /// <c>Nullable&lt;DateTime&gt;</c>), <c>DateTime[]</c> và
    /// <c>Dictionary&lt;string, List&lt;DateTime&gt;&gt;</c> đều serialize ra cùng một giá trị hỏng y
    /// hệt như một field trần. Một rule chỉ so sánh type ngoài cùng chính là phiên bản analyzer này mà
    /// ai cũng viết ra đầu tiên, và nó pass mọi test viết cho một property đơn giản.
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
