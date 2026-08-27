using Microsoft.CodeAnalysis.Testing;
using Nvm.Analyzers;

namespace Nvm.AnalyzerTests;

public sealed class Nvm002DateTimeInContractAnalyzerTests
{
    private static Task VerifyAsync(string source, params DiagnosticResult[] expected) =>
        AnalyzerSnippet.VerifyAsync<Nvm002DateTimeInContractAnalyzer>(source, expected);

    // The span is over the MEMBER NAME, not over the type. For a positional record parameter the
    // generated property's location is the parameter identifier, which is also where a reader's
    // cursor wants to land.
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
            using Nvm.Contracts.Events;

            [EventVersion(1)]
            public sealed record ProbeEvent(
                System.Guid EventId,
                System.DateTimeOffset OccurredAt,
                string SiteId,
                {{members}}) : IDomainEvent;
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
        // The nesting cases are the point. A rule that compares only the outermost type passes the
        // first row here and fails silently on every other one — and a DateTime inside a List is
        // serialized exactly as broken as a bare one.
        var source = Event(member);

        await VerifyAsync(source, Violation(source, "ProbeEvent", "When", displayed));
    }

    [Fact]
    public async Task DateTimeOffsetIsTheSanctionedTypeAndStaysSilent()
    {
        // The control. Without it, an analyzer that flagged every member of every contract would pass
        // every test above.
        await VerifyAsync(Event("System.DateTimeOffset Recorded"));
    }

    [Fact]
    public async Task ATypeInTheContractsNamespaceIsCheckedEvenWhenItIsNotAnEvent()
    {
        // A record that is not an event today becomes the payload of one next milestone, and by then
        // its DateTime is already in rows nobody may rewrite.
        const string source = """
            namespace Nvm.Contracts.Something
            {
                public sealed record Measurement(System.DateTime TakenAt);
            }
            """;

        await VerifyAsync(source, Violation(source, "Measurement", "TakenAt", "DateTime"));
    }

    [Fact]
    public async Task ATypeOutsideTheContractSurfaceIsLeftAlone()
    {
        // NVM002 is about what goes on the wire and into the append-only store. An in-memory helper in
        // some Functional Block is free to use DateTime — refusing it there would make the rule feel
        // arbitrary, which is how rules get suppressed instead of followed.
        await VerifyAsync(
            """
            namespace Nvm.FactoryModel.Internals
            {
                public sealed record Scratch(System.DateTime TakenAt);
            }
            """);
    }

    [Fact]
    public async Task APlainFieldIsCheckedAndItsBackingStoreIsNotReportedTwice()
    {
        // Properties and fields are both read, so a naive implementation reports an auto-property once
        // for the property and once for its compiler-generated backing field. Two errors on one line
        // is how people conclude the analyzer is broken.
        const string source = """
            namespace Nvm.Contracts.Something
            {
                public sealed class Holder
                {
                    public System.DateTime Field;

                    public System.DateTime Property { get; init; }
                }
            }
            """;

        // Exactly two diagnostics. The test framework fails on an unexpected third, which is what
        // makes this a real check on the backing field rather than a restatement of the rule.
        await VerifyAsync(
            source,
            Violation(source, "Holder", "Field", "DateTime"),
            Violation(source, "Holder", "Property", "DateTime"));
    }
}
