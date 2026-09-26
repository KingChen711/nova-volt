using System.Reflection;

namespace Nvm.ArchitectureTests;

/// <summary>K8/K9 cho mọi Functional Block, kể cả block thêm sau M5.</summary>
/// <remarks>
/// Danh sách block lấy từ thư mục <c>src/FunctionalBlocks</c> (mọi project không kết thúc bằng
/// <c>.Hosting</c>), không phải một danh sách viết tay: block mới bị kiểm ngay khi được thêm vào solution.
/// </remarks>
public sealed class AllFunctionalBlocksBoundaryTests
{
    private static readonly string[] Allowed = ["Nvm.Contracts", "Nvm.Kernel", "Nvm.Time"];
    private static readonly string[] Infrastructure =
        ["Microsoft.Data.SqlClient", "Microsoft.EntityFrameworkCore", "Npgsql", "MassTransit", "Quartz"];

    public static TheoryData<string> Blocks()
    {
        var data = new TheoryData<string>();
        foreach (var name in BlockAssemblyNames())
        { data.Add(name); }
        return data;
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public void Block_ReferencesOnlyPlatformAllowlist_AndNoInfrastructure(string assemblyName)
    {
        var assembly = Load(assemblyName);
        var references = NvmAssemblies.NamesReferencedBy(assembly);
        references.Where(name => name.StartsWith("Nvm.", StringComparison.Ordinal))
            .Except(Allowed, StringComparer.Ordinal)
            .ShouldBeEmpty($"{assemblyName} may reference only {string.Join(", ", Allowed)} (K8)");
        references.Intersect(Infrastructure, StringComparer.Ordinal)
            .ShouldBeEmpty($"{assemblyName} must not reference storage or transport (K9)");
    }

    [Fact]
    public void BlockDiscovery_FindsTheKnownBlocks()
    {
        BlockAssemblyNames().ShouldContain("Nvm.Traceability");
        BlockAssemblyNames().ShouldContain("Nvm.Quality");
        BlockAssemblyNames().ShouldContain("Nvm.ProductionExecution");
    }

    private static IReadOnlyList<string> BlockAssemblyNames()
    {
        var root = FindRepoRoot();
        return [.. Directory.EnumerateFiles(Path.Combine(root, "src", "FunctionalBlocks"), "*.csproj",
                SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => !name.EndsWith(".Hosting", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Block mới phải được thêm làm ProjectReference của project test này, nếu không test đỏ.</summary>
    private static Assembly Load(string assemblyName)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (!File.Exists(dll))
        { throw new FileNotFoundException($"Add {assemblyName} as a ProjectReference of Nvm.ArchitectureTests.", dll); }
        return Assembly.LoadFrom(dll);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NovaVolt.Mes.slnx")))
        { directory = directory.Parent; }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
