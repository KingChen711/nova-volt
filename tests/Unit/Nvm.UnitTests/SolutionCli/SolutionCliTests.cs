using Nvm.SolutionCli;

namespace Nvm.UnitTests.SolutionCli;

/// <summary>M13: một solution.yaml, hai mode; version Functional Block được kiểm trước khi sinh cấu hình.</summary>
public sealed class SolutionCliTests
{
    private static readonly Dictionary<string, string> Blocks = new(StringComparer.Ordinal)
    {
        ["Traceability"] = "2.1.0",
        ["Quality"] = "2.0.0",
    };

    [Fact]
    public void TheRepositoryManifest_IsValidAgainstTheBuiltBlocks()
    {
        var root = RepositoryRoot();
        var manifest = SolutionManifest.Parse(File.ReadAllText(Path.Combine(root, "solution.yaml")));
        SolutionValidator.Validate(manifest, BlockCatalog.Load(root)).ShouldBeEmpty();
        manifest.Apps.SelectMany(a => a.FunctionalBlocks).Select(b => b.Name)
            .ShouldBe(BlockCatalog.Load(root).Keys.Order(StringComparer.Ordinal), ignoreOrder: true);
    }

    [Theory]
    [InlineData("2.0.0", null)]                // build 2.1.0 mới hơn cùng major: tương thích
    [InlineData("2.1.0", null)]
    [InlineData("2.2.0", "build chỉ có 2.1.0")]  // khai báo cần tính năng chưa có
    [InlineData("1.9.0", "khác major")]        // contract đã đổi
    [InlineData("3.0.0", "khác major")]
    public void DeclaredVersion_MustShareMajor_AndNotBeNewerThanTheBuild(string declared, string? error)
    {
        var errors = SolutionValidator.Validate(Manifest(("Traceability", declared)), Blocks);
        if (error is null)
        { errors.ShouldBeEmpty(); }
        else
        { errors.Single().ShouldContain(error); }
    }

    [Fact]
    public void UnknownBlock_UnknownMode_AndDanglingExtensionApp_AreErrors()
    {
        var manifest = Manifest(("Warehouse", "1.0.0"));
        manifest.Solution.Mode = "cluster";
        manifest.ExtensionApps.Add(new ExtensionAppSpec { Name = "NvmTrace", Consumes = ["Nvm.App.Quality/pom/v1"] });
        var errors = SolutionValidator.Validate(manifest, Blocks);
        errors.Count.ShouldBe(3);
        errors.ShouldContain(e => e.Contains("Warehouse", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("cluster", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("Nvm.App.Quality", StringComparison.Ordinal));
    }

    [Fact]
    public void SameManifest_Monolith_StartsOnlyInfrastructure_Distributed_AlsoStartsApps()
    {
        var manifest = Manifest(("Traceability", "2.1.0"));
        var monolith = SolutionGenerator.Generate(manifest, "monolith");
        monolith.Select(f => f.Path).ShouldBe(["docker-compose.solution.yml", "run-monolith.sh"]);
        var monolithCompose = monolith[0].Content;
        monolithCompose.ShouldContain("  mssql:");
        monolithCompose.ShouldNotContain("  execution:");
        monolith[1].Content.ShouldContain("dotnet run --project src/Apps/Nvm.Host.All");

        var distributed = SolutionGenerator.Generate(manifest, "distributed");
        distributed.Select(f => f.Path).ShouldBe(["docker-compose.solution.yml", "values.yaml"]);
        distributed[0].Content.ShouldContain("  execution:\n    profiles: [solution]");
        distributed[0].Content.ShouldContain("  projection:");
        distributed[1].Content.ShouldContain("replicas: 2");
        distributed[1].Content.ShouldContain("Traceability: \"2.1.0\"");
    }

    private static SolutionManifest Manifest(params (string Name, string Version)[] blocks)
    {
        var app = new AppSpec { Name = "Nvm.App.Execution", Service = "execution", Image = "novavolt/nvm-execution", Replicas = 2 };
        app.FunctionalBlocks.AddRange(blocks.Select(b => new BlockRef { Name = b.Name, Version = b.Version }));
        var manifest = new SolutionManifest
        {
            Solution = new SolutionInfo { Name = "novavolt-mes", Version = "1.13.0", Mode = "distributed" },
            Infrastructure = ["mssql", "timescale"],
        };
        manifest.Apps.Add(app);
        manifest.Workers.Add(new WorkerSpec { Name = "Nvm.ProjectionWorker", Service = "projection", Image = "novavolt/nvm-projection" });
        manifest.ExtensionApps.Add(new ExtensionAppSpec { Name = "NvmShopFloor", Consumes = ["Nvm.App.Execution/pom/v1"] });
        return manifest;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "solution.yaml")))
            { return directory.FullName; }
        }
        throw new DirectoryNotFoundException("solution.yaml not found.");
    }
}
