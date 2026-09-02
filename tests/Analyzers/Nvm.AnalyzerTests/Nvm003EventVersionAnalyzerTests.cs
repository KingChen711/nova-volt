using Microsoft.CodeAnalysis.Testing;
using Nvm.Analyzers;

namespace Nvm.AnalyzerTests;

public sealed class Nvm003EventVersionAnalyzerTests
{
    private static Task VerifyAsync(string source, params DiagnosticResult[] expected) =>
        AnalyzerSnippet.VerifyAsync<Nvm003EventVersionAnalyzer>(source, expected);

    private static DiagnosticResult Violation(string source, string expression, string type, string fault) =>
        AnalyzerSnippet.Violation(source, expression, Nvm003EventVersionAnalyzer.DiagnosticId, type, fault);

    private static string Event(string attribute) =>
        $$"""
        {{AnalyzerSnippet.ContractTypes}}

        namespace Somewhere.Else
        {
            using Nvm.Contracts.Events;

            {{attribute}}
            public sealed record ProbeEvent(
                System.Guid EventId,
                System.DateTimeOffset OccurredAt,
                string SiteId) : IDomainEvent;
        }
        """;

    [Fact]
    public async Task AnEventWithoutTheAttributeIsRefused()
    {
        // Cái marker trông thừa thãi khi chỉ có một version, và không thể thêm vào được nữa một khi
        // payload v1 đã nằm trong append-only store — đúng lúc đó là lần đầu tiên có người cần đến nó.
        var source = Event(string.Empty);

        // Không có attribute nào để trỏ vào, nên diagnostic rơi vào tên type.
        await VerifyAsync(source, Violation(source, "ProbeEvent", "ProbeEvent", "has no [EventVersion]"));
    }

    [Fact]
    public async Task VersionZeroIsRefusedBecauseItIsWhatNobodyChoosingLooksLike()
    {
        // 0 là giá trị mặc định của một int. Chấp nhận nó có nghĩa là chấp nhận con số duy nhất không
        // mang theo quyết định nào cả.
        var source = Event("[EventVersion(0)]");

        // Span bao trùm chính attribute, không tính dấu ngoặc vuông: đó là AttributeSyntax.
        await VerifyAsync(source, Violation(source, "EventVersion(0)", "ProbeEvent", "declares version 0"));
    }

    [Fact]
    public async Task NegativeVersionsAreRefusedToo()
    {
        var source = Event("[EventVersion(-1)]");

        await VerifyAsync(source, Violation(source, "EventVersion(-1)", "ProbeEvent", "declares version -1"));
    }

    [Fact]
    public async Task VersionOneIsWhatEveryEventStartsAtAndStaysSilent()
    {
        // Ca kiểm chứng đối chứng. Không có nó, một analyzer từ chối mọi event vẫn sẽ pass mọi test ở trên.
        await VerifyAsync(Event("[EventVersion(1)]"));
    }

    [Fact]
    public async Task TheMarkerInterfaceItselfIsNotAnEvent()
    {
        // IDomainEvent không tự implement chính nó, nhưng một interface dẫn xuất thì có — và một
        // interface không mang wire payload nào, nên đòi hỏi một version từ nó là nhiễu.
        await VerifyAsync(
            $$"""
            {{AnalyzerSnippet.ContractTypes}}

            namespace Somewhere.Else
            {
                using Nvm.Contracts.Events;

                public interface IPlantEvent : IDomainEvent;
            }
            """);
    }

    [Fact]
    public async Task AnAbstractBaseIsNotAnEventEither()
    {
        // Không có gì từng được serialize dưới dạng abstract type. Mỗi lớp con cụ thể của nó tự nêu ra
        // version riêng, đó là lý do EventVersionAttribute được khai báo Inherited = false.
        await VerifyAsync(
            $$"""
            {{AnalyzerSnippet.ContractTypes}}

            namespace Somewhere.Else
            {
                using Nvm.Contracts.Events;

                public abstract record PlantEvent(
                    System.Guid EventId,
                    System.DateTimeOffset OccurredAt,
                    string SiteId) : IDomainEvent;
            }
            """);
    }
}
