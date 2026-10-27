using System.Xml.Linq;

namespace Nvm.ArchitectureTests;

/// <summary>Đồ thị ProjectReference của src/ và luật chiều phụ thuộc giữa các lớp.</summary>
public static class LayerRules
{
    /// <summary>Lớp được phép reference những lớp nào (kể cả chính nó).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>
    {
        ["Foundation"] = ["Foundation"],
        ["Product"] = ["Product", "Foundation"],
        ["Project"] = ["Project", "Product", "Foundation"],
        // Phía xưởng và gateway đóng vai hệ bên ngoài: chỉ thấy Foundation, như hệ thật chỉ thấy command và event.
        ["Shopfloor"] = ["Shopfloor", "Foundation"],
        ["Integration"] = ["Integration", "Foundation"],
    };

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NovaVolt.Mes.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Không tìm thấy gốc repo (NovaVolt.Mes.slnx).");
    }

    /// <summary>Cạnh (lớp nguồn, project nguồn, lớp đích, project đích) của mọi project trong src/.</summary>
    public static IEnumerable<(string FromLayer, string From, string ToLayer, string To)> Edges(string root)
    {
        var src = Path.Combine(root, "src");
        foreach (var csproj in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
        {
            var fromLayer = LayerOf(src, csproj);
            foreach (var include in XDocument.Load(csproj).Descendants("ProjectReference")
                         .Select(e => (string?)e.Attribute("Include"))
                         .OfType<string>())
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, include));
                yield return (fromLayer, Path.GetFileNameWithoutExtension(csproj), LayerOf(src, target),
                    Path.GetFileNameWithoutExtension(target));
            }
        }
    }

    public static IEnumerable<string> Violations(IEnumerable<(string FromLayer, string From, string ToLayer, string To)> edges) =>
        edges.Where(e => !Allowed.TryGetValue(e.FromLayer, out var ok) || !ok.Contains(e.ToLayer))
            .Select(e => $"{e.From} ({e.FromLayer}) -> {e.To} ({e.ToLayer})");

    private static string LayerOf(string src, string path)
    {
        var rel = Path.GetRelativePath(src, path);
        return rel.StartsWith("..", StringComparison.Ordinal)
            ? "outside-src"
            : rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
    }
}
