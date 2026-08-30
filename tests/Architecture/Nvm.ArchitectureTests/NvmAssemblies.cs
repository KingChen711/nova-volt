using System.Reflection;
using Nvm.Bus;
using Nvm.Contracts.Events;
using Nvm.FactoryModel;
using Nvm.Kernel.Commands;
using Nvm.Sparkplug;
using Nvm.Time;

namespace Nvm.ArchitectureTests;

/// <summary>The assemblies these rules are about, reached through a type rather than by name.</summary>
/// <remarks>
/// <c>typeof(X).Assembly</c> rather than <c>Assembly.Load("Nvm.Kernel")</c>: a typo in a string
/// produces a rule that throws or, worse, silently examines nothing. A typo in a type name does not
/// compile.
/// </remarks>
internal static class NvmAssemblies
{
    internal static Assembly Contracts => typeof(IDomainEvent).Assembly;

    internal static Assembly Kernel => typeof(ICommandDispatcher).Assembly;

    internal static Assembly Bus => typeof(BusServiceCollectionExtensions).Assembly;

    internal static Assembly FactoryModel => typeof(FactoryModelServiceCollectionExtensions).Assembly;

    internal static Assembly Sparkplug => typeof(SparkplugPayload).Assembly;

    internal static Assembly Time => typeof(IProductionCalendar).Assembly;

    /// <summary>Names an assembly is allowed to reference while still counting as "BCL only".</summary>
    internal static bool IsBcl(string name) =>
        name is "netstandard" or "mscorlib" or "System"
        || name.StartsWith("System.", StringComparison.Ordinal);

    /// <summary>Every assembly name a given assembly references, in order.</summary>
    /// <remarks>
    /// The same caveat as <see cref="NvmReferencesOf"/>: this is what the runtime needs, not what the
    /// csproj lists. A package used for one <c>const</c> is compiled away and does not appear.
    /// </remarks>
    internal static IReadOnlyList<string> NamesReferencedBy(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Order(StringComparer.Ordinal)];

    /// <summary>The <c>Nvm.*</c> assemblies a given assembly references.</summary>
    /// <remarks>
    /// <para>
    /// This is the list the runtime needs, which is <b>not</b> the list of <c>ProjectReference</c>
    /// entries. A reference whose only use was a <c>const</c> is compiled away entirely: the value is
    /// inlined and the assembly never appears in metadata. Measured on <c>Nvm.Bus</c>, which
    /// references <c>Nvm.Hosting</c> in its csproj purely for <c>HealthTags.Ready</c> and lists only
    /// <c>Nvm.Contracts</c> here.
    /// </para>
    /// <para>
    /// That is the right list for these rules anyway. What K8 and K9 forbid is one component being
    /// able to <i>call</i> another, and a dependency the compiler erased cannot be called. A rule that
    /// read the csproj instead would fail a block for borrowing one constant.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> NvmReferencesOf(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Nvm.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
}
