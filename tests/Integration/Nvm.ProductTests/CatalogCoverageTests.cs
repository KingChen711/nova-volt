using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Nvm.Product;

namespace Nvm.ProductTests;

/// <summary>DoD R3: mọi entity của app Production đọc được qua đường OData; mọi command công bố đều có test gọi nó.</summary>
public sealed partial class CatalogCoverageTests
{
    [Fact]
    public async Task Every_entity_set_of_the_production_app_is_readable()
    {
        var p = await ProductHarness.CreateAsync();
        var app = ProductHarness.Registry.AppOf(ProductionApp.Name);

        using var session = p.Executor.OpenRead();
        foreach (var set in app.Objects)
        {
            ProductHarness.Registry.EntitySetIn(ProductionApp.Name, set.Name).ShouldBe(set);
            Should.NotThrow(() => session.Set(set).Cast<object>().Take(1).ToList(), set.Name);
        }

        var declared = ProductHarness.Registry.Entities.Select(e => e.Type).Where(t => t.Assembly == typeof(ProductionApp).Assembly);
        app.Objects.ShouldBe(declared, ignoreOrder: true);
    }

    [Fact]
    public void Every_published_command_is_exercised_by_a_test()
    {
        var sources = string.Join('\n', Directory.EnumerateFiles(SourceDirectory(), "*.cs").Select(File.ReadAllText));
        var used = NewCommand().Matches(sources).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var missing = ProductHarness.Registry.AppOf(ProductionApp.Name).Commands.Select(c => c.Name).Where(n => !used.Contains(n)).ToList();
        missing.ShouldBeEmpty();
    }

    /// <summary>Thư mục chứa file nguồn này, lấy lúc biên dịch: binary của test nằm ở artifacts/, ngoài cây nguồn.</summary>
    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [GeneratedRegex(@"new\s+([A-Z][A-Za-z]+)\s*(?:\{|\()")]
    private static partial Regex NewCommand();
}
