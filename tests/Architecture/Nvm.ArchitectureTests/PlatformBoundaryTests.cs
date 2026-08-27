using NetArchTest.Rules;

namespace Nvm.ArchitectureTests;

/// <summary>A1, A2 — what the Platform layer is allowed to know about.</summary>
/// <remarks>
/// Every rule here comes with a <b>positive control</b>: the same check applied to an assembly that
/// is supposed to fail it. Without one, a rule that examines the wrong thing — an empty type list, a
/// misspelled namespace prefix — reports "no violations found" and stays green forever. That is the
/// architecture-test version of the silent analyzer in C15.1.
/// </remarks>
public sealed class PlatformBoundaryTests
{
    /// <summary>Infrastructure that domain code may not touch. AGENTS.md K9.</summary>
    private static readonly string[] Infrastructure =
    [
        "MassTransit",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Microsoft.Data.SqlClient",
    ];

    [Fact]
    public void A1_Contracts_ReferencesNothingButTheBcl()
    {
        // Nvm.Contracts is the bottom of the dependency tree, and it is the assembly a Mendix
        // developer or an external consumer would be handed. One package reference here becomes a
        // package reference for everybody downstream, forever.
        var outsiders = NvmAssemblies.Contracts
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !NvmAssemblies.IsBcl(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        outsiders.ShouldBeEmpty(
            $"Nvm.Contracts must stay BCL-only; it references {string.Join(", ", outsiders)}");
    }

    [Fact]
    public void A1_Control_TheBclFilterCanActuallySeeANonBclReference()
    {
        // If IsBcl said "yes" to everything, or GetReferencedAssemblies came back empty, A1 above
        // would pass while checking nothing at all.
        NvmAssemblies.Bus
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !NvmAssemblies.IsBcl(name))
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void A2_Kernel_DoesNotKnowAboutTransportOrDatabases()
    {
        // The command pipeline has to be testable without a broker or a database. It does reference
        // Microsoft.Extensions.DependencyInjection.Abstractions, and that is not a violation: K9
        // forbids infrastructure, not the ability to register a handler.
        var result = Types.InAssembly(NvmAssemblies.Kernel)
            .ShouldNot()
            .HaveDependencyOnAny(Infrastructure)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Nvm.Kernel depends on infrastructure through: {Describe(result)}");
    }

    [Fact]
    public void A2_Control_TheDependencyCheckCanActuallyFindMassTransit()
    {
        // Nvm.Bus depends on MassTransit by design. If this passes, HaveDependencyOnAny is not
        // looking where it claims to look, and A2 above proves nothing.
        Types.InAssembly(NvmAssemblies.Bus)
            .ShouldNot()
            .HaveDependencyOnAny("MassTransit")
            .GetResult()
            .IsSuccessful.ShouldBeFalse();
    }

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.FailingTypeNames is null
            ? "(no type names reported)"
            : string.Join(", ", result.FailingTypeNames);
}
