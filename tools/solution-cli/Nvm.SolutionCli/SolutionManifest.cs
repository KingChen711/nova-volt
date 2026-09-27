using System.Globalization;
using System.Text;
using System.Xml.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nvm.SolutionCli;

public sealed class SolutionManifest
{
    public SolutionInfo Solution { get; set; } = new();
    public List<string> Infrastructure { get; set; } = [];
    public List<AppSpec> Apps { get; set; } = [];
    public List<WorkerSpec> Workers { get; set; } = [];
    public List<ExtensionAppSpec> ExtensionApps { get; set; } = [];

    public static SolutionManifest Parse(string yaml) =>
        new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build()
            .Deserialize<SolutionManifest>(yaml) ?? throw new InvalidDataException("solution.yaml is empty.");
}

public sealed class SolutionInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Mode { get; set; } = "";
}

public sealed class AppSpec
{
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public string Image { get; set; } = "";
    public int Port { get; set; } = 8080;
    public int Replicas { get; set; } = 1;
    public List<BlockRef> FunctionalBlocks { get; set; } = [];
    public PomSpec? PublicObjectModel { get; set; }
}

public sealed class BlockRef
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class PomSpec
{
    public string Route { get; set; } = "";
    public string OdataVersion { get; set; } = "";
}

public sealed class WorkerSpec
{
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public string Image { get; set; } = "";
    public int Replicas { get; set; } = 1;
}

public sealed class ExtensionAppSpec
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> Consumes { get; set; } = [];
}

/// <summary>Version thật của từng Functional Block, đọc từ csproj.</summary>
public static class BlockCatalog
{
    public static IReadOnlyDictionary<string, string> Load(string repositoryRoot)
    {
        var root = Path.Combine(repositoryRoot, "src", "FunctionalBlocks");
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in Directory.EnumerateFiles(root, "Nvm.*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project)["Nvm.".Length..];
            if (name.EndsWith(".Hosting", StringComparison.Ordinal))
            { continue; }
            var version = XDocument.Load(project).Descendants("Version").FirstOrDefault()?.Value;
            blocks[name] = version ?? "";
        }
        return blocks;
    }
}

public static class SolutionValidator
{
    /// <summary>
    /// Luật tương thích: App khai báo version X.Y.Z thì FB thật phải cùng major và không cũ hơn X.Y.Z (thêm minor/patch
    /// là tương thích ngược; đổi major là phá contract). FB lạ, App trùng tên, mode lạ đều là lỗi.
    /// </summary>
    public static IReadOnlyList<string> Validate(SolutionManifest manifest, IReadOnlyDictionary<string, string> blocks)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(blocks);
        List<string> errors = [];
        if (manifest.Solution.Mode is not ("monolith" or "distributed"))
        { errors.Add($"solution.mode phải là monolith hoặc distributed, không phải '{manifest.Solution.Mode}'."); }
        if (!Version.TryParse(manifest.Solution.Version, out _))
        { errors.Add($"solution.version '{manifest.Solution.Version}' không phải SemVer."); }
        var services = manifest.Apps.Select(a => a.Service).Concat(manifest.Workers.Select(w => w.Service)).ToArray();
        foreach (var duplicate in services.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1))
        { errors.Add($"Service '{duplicate.Key}' khai báo nhiều lần."); }
        foreach (var app in manifest.Apps)
        {
            if (app.FunctionalBlocks.Count == 0)
            { errors.Add($"{app.Name} không có Functional Block nào."); }
            foreach (var block in app.FunctionalBlocks)
            {
                if (!blocks.TryGetValue(block.Name, out var actual))
                { errors.Add($"{app.Name}: không có Functional Block '{block.Name}' trong src/FunctionalBlocks."); continue; }
                if (!Version.TryParse(block.Version, out var wanted) || !Version.TryParse(actual, out var have))
                { errors.Add($"{app.Name}: version của {block.Name} không đọc được (khai báo '{block.Version}', csproj '{actual}')."); continue; }
                if (wanted.Major != have.Major)
                { errors.Add($"{app.Name}: {block.Name} khai báo {block.Version} nhưng build là {actual} — khác major, contract đã đổi."); }
                else if (have < wanted)
                { errors.Add($"{app.Name}: {block.Name} khai báo {block.Version} nhưng build chỉ có {actual}."); }
            }
            foreach (var duplicate in app.FunctionalBlocks.GroupBy(b => b.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            { errors.Add($"{app.Name}: {duplicate.Key} khai báo nhiều lần."); }
        }
        var appNames = manifest.Apps.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var extension in manifest.ExtensionApps)
        {
            foreach (var consumed in extension.Consumes.Where(c => !appNames.Contains(c.Split('/')[0])))
            { errors.Add($"{extension.Name} dùng '{consumed}' nhưng không App nào tên như vậy."); }
        }
        return errors;
    }

    public static string Matrix(SolutionManifest manifest, IReadOnlyDictionary<string, string> blocks)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(blocks);
        var text = new StringBuilder("| App | Functional Block | Khai báo | Build |\n|---|---|---|---|\n");
        foreach (var app in manifest.Apps)
        {
            foreach (var block in app.FunctionalBlocks)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"| {app.Name} | {block.Name} | {block.Version} | {blocks.GetValueOrDefault(block.Name, "—")} |\n");
            }
        }
        return text.ToString();
    }
}
