using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nvm.Analyzers;

/// <summary>Từ chối mọi lần đọc machine clock. AGENTS.md K1.</summary>
/// <remarks>
/// <para>
/// <b>Vì sao đây là một build error chứ không phải một ghi chú code review.</b> Formation chạy hàng
/// giờ và aging chạy hàng tuần. Một saga chờ mười hai ngày chỉ có thể được test bằng cách dịch chuyển
/// đồng hồ, và một đồng hồ đọc từ một static property thì không thể dịch chuyển được — test sẽ phải
/// chờ mười hai ngày thật, nên nó không bao giờ được viết ra, và đường dẫn mười hai ngày đó ship mà
/// chưa từng được thực thi.
/// </para>
/// <para>
/// <c>TimeProvider</c> là seam. Được inject, nó là <c>TimeProvider.System</c> trong production và
/// <c>FakeTimeProvider</c> trong một test bỏ qua hai tuần chỉ trong một mili-giây.
/// </para>
/// <para>
/// Việc khớp được thực hiện trên <b>symbol</b>, không phải trên text. Một kiểm tra cú pháp cho chuỗi
/// <c>"DateTime.UtcNow"</c> bị đánh bại bởi <c>using Clock = System.DateTime;</c> hoặc bởi một tên đủ
/// điều kiện, và một analyzer dễ dàng né tránh sẽ dạy người ta né nó.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Nvm001DateTimeNowAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Id của diagnostic, đúng như nó xuất hiện trong build output và trong .editorconfig.</summary>
    public const string DiagnosticId = "NVM001";

    private const string Category = "Nvm.Determinism";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Reading the machine clock is forbidden",
        // RS1032: hoặc một câu không có dấu chấm cuối, hoặc nhiều câu với một dấu chấm. Message phải
        // nói rõ nên làm gì thay vào đó — một diagnostic chỉ nói "forbidden" thì bị suppress, không
        // được sửa.
        messageFormat:
            "'{0}.{1}' reads the machine clock. Inject TimeProvider and call GetUtcNow() instead, "
            + "so that a saga spanning days stays testable in milliseconds (AGENTS.md K1).",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Time must arrive through TimeProvider so that FakeTimeProvider can move it. Formation "
            + "and aging span days; a path that can only be reached by waiting is a path nobody tests.");

    // Các member trả về "now" mà không được hỏi now đến từ đâu. DateTimeOffset không có Today, nên
    // các cặp này không đối xứng — vì lý do đó chúng được liệt kê ra thay vì được suy luận.
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

        // Generated code được miễn trừ. Không ai có thể sửa một vi phạm trong một file bị viết lại
        // trên mỗi lần build, và kết quả duy nhất sẽ là người ta tắt hẳn cả rule đi.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        // Các type được resolve một lần cho mỗi compilation thay vì một lần cho mỗi property
        // reference. Phương án thay thế là so sánh tên type đầy đủ dưới dạng chuỗi trên mọi node
        // trong codebase.
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
