using Microsoft.CodeAnalysis.Testing;
using Nvm.Analyzers;

namespace Nvm.AnalyzerTests;

public sealed class Nvm001DateTimeNowAnalyzerTests
{
    private static Task VerifyAsync(string source, params DiagnosticResult[] expected) =>
        AnalyzerSnippet.VerifyAsync<Nvm001DateTimeNowAnalyzer>(source, expected);

    private static DiagnosticResult Violation(string source, string expression, string type, string member) =>
        AnalyzerSnippet.Violation(source, expression, Nvm001DateTimeNowAnalyzer.DiagnosticId, type, member);

    [Theory]
    [InlineData("DateTime", "UtcNow")]
    [InlineData("DateTime", "Now")]
    [InlineData("DateTime", "Today")]
    [InlineData("DateTimeOffset", "UtcNow")]
    [InlineData("DateTimeOffset", "Now")]
    public async Task EveryAmbientClockMemberIsRefused(string type, string member)
    {
        // All five, not only the one everybody writes. An analyzer that catches DateTime.UtcNow and
        // nothing else moves the problem to DateTimeOffset.Now, where it is harder to spot because it
        // looks like it was chosen deliberately.
        var expression = $"System.{type}.{member}";
        var source = $$"""
            class Probe
            {
                object Read() => {{expression}};
            }
            """;

        await VerifyAsync(source, Violation(source, expression, type, member));
    }

    [Fact]
    public async Task AnAliasDoesNotHideTheClock()
    {
        // The reason this analyzer matches symbols rather than text. A rule that greps for
        // "DateTime.UtcNow" is beaten by one using directive, and a rule that is trivially avoidable
        // teaches people to avoid it rather than to fix the code. Note the message still names the
        // real type, not the alias — otherwise the person reading it goes looking for a type called
        // Clock.
        const string source = """
            using Clock = System.DateTime;

            class Probe
            {
                object Read() => Clock.UtcNow;
            }
            """;

        await VerifyAsync(source, Violation(source, "Clock.UtcNow", "DateTime", "UtcNow"));
    }

    [Fact]
    public async Task TimeProviderIsTheSanctionedWayAndStaysSilent()
    {
        // The control. Without it, an analyzer that flagged every property reference in the codebase
        // would still pass every test above.
        await VerifyAsync(
            """
            class Probe
            {
                object Read(System.TimeProvider clock) => clock.GetUtcNow();
            }
            """);
    }

    [Fact]
    public async Task AnUnrelatedNowPropertyIsNotTheMachineClock()
    {
        // Same name, different meaning. Matching on the member name alone would refuse a perfectly
        // good domain property and turn the rule into noise that people switch off.
        await VerifyAsync(
            """
            class Shift
            {
                public string Now => "A";
            }

            class Probe
            {
                object Read(Shift shift) => shift.Now;
            }
            """);
    }
}
