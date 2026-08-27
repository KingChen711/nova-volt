using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Nvm.AnalyzerTests;

/// <summary>Compiles a snippet with one analyzer and checks exactly which diagnostics come out.</summary>
/// <remarks>
/// <para>
/// <c>DefaultVerifier</c>, not <c>XUnitVerifier</c>: the <c>.XUnit</c> flavour of the testing package
/// is built on xunit v2 and this repo is v3 only. A failed assertion still throws and still fails the
/// test — what is lost is xunit-shaped failure formatting, which is not worth a second test framework.
/// </para>
/// <para>
/// The expected spans are <b>found in the source</b> rather than written by hand. Hand-counted line
/// and column numbers are wrong the first time and wrong again after any edit above them, and the
/// failure they produce reads exactly like the analyzer being broken.
/// </para>
/// </remarks>
internal static class AnalyzerSnippet
{
    /// <summary>The contract types an event snippet needs, since a snippet references no project.</summary>
    /// <remarks>
    /// Declared here rather than referenced, and that is deliberate: it exercises the fact that both
    /// analyzers resolve these types <b>by name</b>. Rename one of them for real and the rules go
    /// silent — a snippet that got them from a project reference would never notice.
    /// </remarks>
    public const string ContractTypes = """
        namespace Nvm.Contracts.Events
        {
            public interface IDomainEvent
            {
                System.Guid EventId { get; }
                System.DateTimeOffset OccurredAt { get; }
                string SiteId { get; }
            }

            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
            public sealed class EventVersionAttribute : System.Attribute
            {
                public EventVersionAttribute(int version) => Version = version;

                public int Version { get; }
            }
        }
        """;

    public static async Task VerifyAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,

            // The snippets mention System.TimeProvider and System.Collections.Generic, which the
            // package's default reference set does not carry.
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        test.ExpectedDiagnostics.AddRange(expected);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Expects <paramref name="id"/> over the first occurrence of <paramref name="expression"/>.</summary>
    public static DiagnosticResult Violation(string source, string expression, string id, params object[] arguments)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var line = Array.FindIndex(lines, text => text.Contains(expression, StringComparison.Ordinal));

        if (line < 0)
        {
            throw new ArgumentException($"'{expression}' is not in the snippet.", nameof(expression));
        }

        var column = lines[line].IndexOf(expression, StringComparison.Ordinal);

        return new DiagnosticResult(id, DiagnosticSeverity.Error)
            .WithSpan(line + 1, column + 1, line + 1, column + 1 + expression.Length)
            .WithArguments(arguments);
    }
}
