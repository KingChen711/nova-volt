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
        // The marker looks redundant while there is only one version, and is impossible to add once
        // v1 payloads are already in an append-only store — which is exactly when somebody first
        // wants it.
        var source = Event(string.Empty);

        // No attribute to point at, so the diagnostic lands on the type name.
        await VerifyAsync(source, Violation(source, "ProbeEvent", "ProbeEvent", "has no [EventVersion]"));
    }

    [Fact]
    public async Task VersionZeroIsRefusedBecauseItIsWhatNobodyChoosingLooksLike()
    {
        // 0 is the default value of an int. Accepting it means accepting the one number that carries
        // no decision.
        var source = Event("[EventVersion(0)]");

        // The span covers the attribute itself, without the brackets: that is the AttributeSyntax.
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
        // The control. Without it, an analyzer that refused every event would pass every test above.
        await VerifyAsync(Event("[EventVersion(1)]"));
    }

    [Fact]
    public async Task TheMarkerInterfaceItselfIsNotAnEvent()
    {
        // IDomainEvent does not implement itself, but a derived interface would — and an interface
        // carries no wire payload, so demanding a version from one is noise.
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
        // Nothing is ever serialized as the abstract type. Its concrete children each state their own
        // version, which is why EventVersionAttribute is declared Inherited = false.
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
