using System.Reflection;

namespace Nvm.ArchitectureTests;

/// <summary>A3, A6 — what a Functional Block is allowed to reach for.</summary>
/// <remarks>
/// <para>
/// A Functional Block is a bounded context with its own entities, commands and schema. It talks to
/// other blocks through <b>contracts and the bus</b>, never by calling them. The moment one block
/// references another, the two share a deployment, a release cadence and a failure — and the word
/// that meant one thing in each of them now has to mean one thing in both (AGENTS.md K8).
/// </para>
/// <para>
/// Stated as an <b>allowlist</b>, not as a list of banned blocks. A denylist has to be edited every
/// time a block is added, by whoever adds it, in a file they have no reason to open — so the first
/// block that is forgotten is the one the rule was for. An allowlist refuses anything new by default.
/// </para>
/// </remarks>
public sealed class FunctionalBlockBoundaryTests
{
    /// <summary>The only <c>Nvm.*</c> assemblies a Functional Block may reference.</summary>
    private static readonly string[] AllowedForFunctionalBlocks = ["Nvm.Contracts", "Nvm.Kernel"];

    [Fact]
    public void A3_FactoryModel_ReferencesOnlyContractsAndKernel()
    {
        var forbidden = NvmAssemblies.NvmReferencesOf(NvmAssemblies.FactoryModel)
            .Where(name => !AllowedForFunctionalBlocks.Contains(name, StringComparer.Ordinal))
            .ToList();

        forbidden.ShouldBeEmpty(
            $"A Functional Block may reference {string.Join(" and ", AllowedForFunctionalBlocks)} only; "
            + $"Nvm.FactoryModel also references {string.Join(", ", forbidden)}");
    }

    [Fact]
    public void A3_Control_TheAllowlistCanActuallyRejectSomething()
    {
        // Applied to this test assembly, which references Nvm.Bus and Nvm.FactoryModel and is
        // emphatically not a Functional Block. If this came back empty, A3 would be an assertion that
        // an empty list is empty.
        NvmAssemblies.NvmReferencesOf(Assembly.GetExecutingAssembly())
            .Where(name => !AllowedForFunctionalBlocks.Contains(name, StringComparer.Ordinal))
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void A3_Control_TheReferenceReaderSeesSomethingAtAll()
    {
        // Guards the case where NvmReferencesOf returns nothing because the prefix is misspelled or
        // the compiler trimmed an unused reference away. FactoryModel genuinely uses both.
        NvmAssemblies.NvmReferencesOf(NvmAssemblies.FactoryModel)
            .ShouldBe(AllowedForFunctionalBlocks);
    }

    [Fact]
    public void A6_FactoryModel_DoesNotChooseItsOwnTransport()
    {
        // A special case of A3, kept separate because the reason is different. A Functional Block
        // produces an event; deciding that the event travels over RabbitMQ is the host's business.
        // A block that references Nvm.Bus cannot be unit tested without a broker, and cannot be
        // reused by a host that publishes some other way.
        NvmAssemblies.NvmReferencesOf(NvmAssemblies.FactoryModel)
            .ShouldNotContain("Nvm.Bus");
    }

    [Fact]
    public void A6_Control_TheCheckWouldSeeNvmBusIfItWereThere()
    {
        // Something in the solution does reference Nvm.Bus, and this proves the name being searched
        // for is spelled the way the assembly actually is.
        Assembly.GetExecutingAssembly()
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ShouldContain("Nvm.Bus");
    }
}
