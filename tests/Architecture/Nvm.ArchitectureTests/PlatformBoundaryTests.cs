using System.Reflection;
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

    [Fact]
    public void A7_TheGeneratedSparkplugTypesDoNotLeaveNvmSparkplug()
    {
        // ADR-026 vendors sparkplug_b.proto and generates C# from it, which means Org.Eclipse.Tahu.*
        // is a shape this repository does not control. One of those types in a public signature makes
        // every caller depend on a file we are not allowed to edit, and the day the specification
        // moves the change arrives everywhere at once.
        var leaks = NvmAssemblies.Sparkplug
            .GetExportedTypes()
            .Where(type => string.Equals(type.Namespace, "Nvm.Sparkplug", StringComparison.Ordinal))
            .SelectMany(MembersExposingGeneratedTypes)
            .Order(StringComparer.Ordinal)
            .ToList();

        leaks.ShouldBeEmpty(
            $"generated Sparkplug types reach callers through: {string.Join(", ", leaks)}");
    }

    [Fact]
    public void A7_Control_TheGeneratedTypeWalkCanActuallyFindOne()
    {
        // Applied to a generated type, the same walk must come back full. Without this, a misspelled
        // namespace in either the filter or the predicate reports "no leaks" and stays green.
        var generated = NvmAssemblies.Sparkplug.GetType("Org.Eclipse.Tahu.Protobuf.Payload", throwOnError: true)!;

        MembersExposingGeneratedTypes(generated).ShouldNotBeEmpty();
    }

    [Fact]
    public void A7_OnlyNvmSparkplugKnowsThereIsProtobufAtAll()
    {
        // The reference-level half. A6 stops a Functional Block choosing its own transport; this stops
        // one choosing its own device codec — C08 and C12 consume readings, not payloads.
        foreach (var assembly in new[]
        {
            NvmAssemblies.Contracts,
            NvmAssemblies.Kernel,
            NvmAssemblies.Bus,
            NvmAssemblies.FactoryModel,
        })
        {
            NvmAssemblies.NamesReferencedBy(assembly).ShouldNotContain(
                "Google.Protobuf",
                $"{assembly.GetName().Name} references Google.Protobuf; decoding belongs to Nvm.Sparkplug");
        }

        // Doubles as the control: the one assembly that is supposed to reference it, does.
        NvmAssemblies.NamesReferencedBy(NvmAssemblies.Sparkplug).ShouldContain("Google.Protobuf");
    }

    /// <summary>Names the public members of a type whose signature mentions a generated type.</summary>
    private static IEnumerable<string> MembersExposingGeneratedTypes(Type type)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        foreach (var property in type.GetProperties(Public).Where(p => IsGenerated(p.PropertyType)))
        {
            yield return $"{type.Name}.{property.Name}";
        }

        foreach (var method in type.GetMethods(Public | BindingFlags.DeclaredOnly))
        {
            if (IsGenerated(method.ReturnType))
            {
                yield return $"{type.Name}.{method.Name}() returns";
            }

            foreach (var parameter in method.GetParameters().Where(p => IsGenerated(p.ParameterType)))
            {
                yield return $"{type.Name}.{method.Name}({parameter.Name})";
            }
        }

        foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()).Where(p => IsGenerated(p.ParameterType)))
        {
            yield return $"{type.Name}..ctor({parameter.Name})";
        }
    }

    /// <summary>Whether a type comes from the vendored schema, however deeply nested.</summary>
    /// <remarks>
    /// The recursion matters more than the direct case: <c>MessageParser&lt;Payload&gt;</c> lives in
    /// <c>Google.Protobuf</c> and would pass a check that only looked at the outermost namespace,
    /// while handing the caller a <c>Payload</c> all the same.
    /// </remarks>
    private static bool IsGenerated(Type type) =>
        (type.Namespace?.StartsWith("Org.Eclipse.Tahu", StringComparison.Ordinal) ?? false)
        || (type.IsArray && IsGenerated(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(IsGenerated));

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.FailingTypeNames is null
            ? "(no type names reported)"
            : string.Join(", ", result.FailingTypeNames);
}
