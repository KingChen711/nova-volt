using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Nvm.Analyzers;

namespace Nvm.AnalyzerTests;

public sealed class Nvm001DateTimeNowAnalyzerTests
{
    // DefaultVerifier, not XUnitVerifier: the .XUnit flavour of the testing package is built on
    // xunit v2 and this repo is v3 only. A failing assertion still throws and still fails the test —
    // what is lost is xunit-shaped failure formatting, which is not worth a second test framework.
    private static async Task VerifyAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<Nvm001DateTimeNowAnalyzer, DefaultVerifier>
        {
            TestCode = source,

            // The control snippet mentions System.TimeProvider, which is absent from the default
            // reference set this package assumes.
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        test.ExpectedDiagnostics.AddRange(expected);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Locates <paramref name="expression"/> in the snippet and expects NVM001 over it.</summary>
    /// <remarks>
    /// Found rather than counted. Hand-written line and column numbers are wrong the first time and
    /// wrong again after any edit to the snippet above them, and the failure they produce says
    /// "expected a diagnostic here" — which reads exactly like the analyzer being broken.
    /// </remarks>
    private static DiagnosticResult Violation(string source, string expression, string type, string member)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var line = Array.FindIndex(lines, text => text.Contains(expression, StringComparison.Ordinal));
        var column = lines[line].IndexOf(expression, StringComparison.Ordinal);

        return new DiagnosticResult(Nvm001DateTimeNowAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithSpan(line + 1, column + 1, line + 1, column + 1 + expression.Length)
            .WithArguments(type, member);
    }

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
