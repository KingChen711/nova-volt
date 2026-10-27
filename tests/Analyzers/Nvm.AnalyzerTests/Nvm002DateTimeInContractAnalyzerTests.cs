using Microsoft.CodeAnalysis.Testing;
using Nvm.Analyzers;

namespace Nvm.AnalyzerTests;

public sealed class Nvm002DateTimeInContractAnalyzerTests
{
    private static Task VerifyAsync(string source, params DiagnosticResult[] expected) =>
        AnalyzerSnippet.VerifyAsync<Nvm002DateTimeInContractAnalyzer>(source, expected);

    // Span nằm trên TÊN MEMBER, không phải trên type. Với một positional record parameter, vị trí
    // của property được sinh ra chính là identifier của parameter, cũng đúng là nơi con trỏ của
    // người đọc muốn dừng lại.
    private static DiagnosticResult Violation(string source, string type, string member, string memberType) =>
        AnalyzerSnippet.Violation(
            source,
            member,
            Nvm002DateTimeInContractAnalyzer.DiagnosticId,
            type,
            member,
            memberType);

    private static string Event(string members) =>
        $$"""
        {{AnalyzerSnippet.ContractTypes}}

        namespace Somewhere.Else
        {
            using Nvm.Foundation;

            [EventVersion(1)]
            public sealed record ProbeEvent(
                System.Guid EventId,
                System.DateTimeOffset OccurredAt,
                string SiteId,
                {{members}}) : IFoundationEvent;
        }
        """;

    [Theory]
    [InlineData("System.DateTime When", "DateTime")]
    [InlineData("System.DateTime? When", "DateTime?")]
    [InlineData("System.DateTime[] When", "DateTime[]")]
    [InlineData("System.Collections.Generic.IReadOnlyList<System.DateTime> When", "IReadOnlyList<DateTime>")]
    [InlineData("System.Collections.Generic.Dictionary<string, System.DateTime[]> When", "Dictionary<string, DateTime[]>")]
    public async Task DateTimeIsRefusedHoweverDeeplyItIsBuried(string member, string displayed)
    {
        // Các trường hợp lồng nhau chính là điểm mấu chốt. Một rule chỉ so sánh type ngoài cùng sẽ
        // pass dòng đầu tiên ở đây và fail âm thầm ở mọi dòng còn lại — và một DateTime bên trong
        // List bị serialize hỏng y hệt như một DateTime trần.
        var source = Event(member);

        await VerifyAsync(source, Violation(source, "ProbeEvent", "When", displayed));
    }

    [Fact]
    public async Task DateTimeOffsetIsTheSanctionedTypeAndStaysSilent()
    {
        // Ca kiểm chứng đối chứng. Không có nó, một analyzer đánh dấu mọi member của mọi contract vẫn
        // sẽ pass mọi test ở trên.
        await VerifyAsync(Event("System.DateTimeOffset Recorded"));
    }

    [Fact]
    public async Task AnEntityIsCheckedBecauseItsColumnsArePersisted()
    {
        // Entity và facet là cột của DB MOM, đọc lại nhiều năm sau như event.
        const string source = $$"""
            {{AnalyzerSnippet.ContractTypes}}

            namespace Somewhere.Else
            {
                public sealed class Pallet : Nvm.Foundation.Entity
                {
                    public System.DateTime PackedOn { get; set; }
                }
            }
            """;

        await VerifyAsync(source, Violation(source, "Pallet", "PackedOn", "DateTime"));
    }

    [Fact]
    public async Task ATypeOutsideTheContractSurfaceIsLeftAlone()
    {
        // NVM002 là về những gì lên bus và xuống bảng. Một helper in-memory trong một
        // Functional Block nào đó được tự do dùng DateTime — từ chối nó ở đó sẽ khiến rule cảm giác
        // tùy tiện, và đó chính là cách rule bị suppress thay vì được tuân theo.
        await VerifyAsync(
            """
            namespace Nvm.Product.Internals
            {
                public sealed record Scratch(System.DateTime TakenAt);
            }
            """);
    }

    [Fact]
    public async Task APlainFieldIsCheckedAndItsBackingStoreIsNotReportedTwice()
    {
        // Cả property lẫn field đều được đọc, nên một implementation ngây thơ sẽ báo cáo một
        // auto-property hai lần: một lần cho property và một lần cho backing field do compiler sinh
        // ra. Hai lỗi trên một dòng là cách người ta kết luận analyzer bị hỏng.
        const string source = $$"""
            {{AnalyzerSnippet.ContractTypes}}

            namespace Somewhere.Else
            {
                public sealed class Holder : Nvm.Foundation.Entity
                {
                    public System.DateTime Field;

                    public System.DateTime Property { get; init; }
                }
            }
            """;

        // Đúng hai diagnostic. Test framework fail nếu có một cái thứ ba bất ngờ, đó chính là điều
        // khiến đây là một kiểm tra thật sự trên backing field thay vì chỉ lặp lại rule.
        await VerifyAsync(
            source,
            Violation(source, "Holder", "Field", "DateTime"),
            Violation(source, "Holder", "Property", "DateTime"));
    }
}
