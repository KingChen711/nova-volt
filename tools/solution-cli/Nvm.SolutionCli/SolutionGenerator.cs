using System.Globalization;
using System.Text;

namespace Nvm.SolutionCli;

/// <summary>Một file sinh ra: đường dẫn tương đối trong thư mục output và nội dung.</summary>
public sealed record GeneratedFile(string Path, string Content);

/// <summary>
/// Sinh cấu hình cho hai mode từ cùng một manifest (scope §5.4, §12.4). Code không đổi giữa hai mode; chỉ đổi cái gì chạy
/// trong container và cái gì chạy trên máy dev.
/// </summary>
public static class SolutionGenerator
{
    public const string Profile = "solution";

    public static IReadOnlyList<GeneratedFile> Generate(SolutionManifest manifest, string mode)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return mode switch
        {
            "monolith" => [Compose(manifest, manifest.Infrastructure), MonolithScript(manifest)],
            "distributed" => [Compose(manifest, [.. manifest.Infrastructure, .. manifest.Apps.Select(a => a.Service),
                .. manifest.Workers.Select(w => w.Service)]), HelmValues(manifest)],
            _ => throw new ArgumentException($"Mode '{mode}' không có; dùng monolith hoặc distributed.", nameof(mode)),
        };
    }

    /// <summary>
    /// Overlay cho docker-compose.yml gốc: gắn profile <c>solution</c> cho đúng các service mode này cần, để
    /// <c>docker compose -f docker-compose.yml -f &lt;file&gt; --profile solution up</c> không bật thừa gì.
    /// </summary>
    private static GeneratedFile Compose(SolutionManifest manifest, IEnumerable<string> services)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Sinh bởi tools/solution-cli từ solution.yaml ({manifest.Solution.Name} {manifest.Solution.Version}). Không sửa tay.")
            .AppendLine("services:");
        foreach (var service in services.Distinct(StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {service}:")
                .AppendLine(CultureInfo.InvariantCulture, $"    profiles: [{Profile}]");
        }
        return new GeneratedFile("docker-compose.solution.yml", Lf(text));
    }

    private static GeneratedFile MonolithScript(SolutionManifest manifest)
    {
        var blocks = manifest.Apps.SelectMany(a => a.FunctionalBlocks).Select(b => $"{b.Name}@{b.Version}")
            .Distinct(StringComparer.Ordinal);
        var text = new StringBuilder()
            .AppendLine("#!/bin/sh")
            .AppendLine(CultureInfo.InvariantCulture, $"# Sinh bởi tools/solution-cli: {manifest.Solution.Name} {manifest.Solution.Version}, mode monolith.")
            .AppendLine(CultureInfo.InvariantCulture, $"# Mọi Functional Block chạy trong một process Nvm.Host.All: {string.Join(", ", blocks)}.")
            .AppendLine("set -e")
            .AppendLine("docker compose -f docker-compose.yml -f \"$(dirname \"$0\")/docker-compose.solution.yml\" --profile solution up -d --wait")
            .AppendLine("exec dotnet run --project src/Apps/Nvm.Host.All -- \"$@\"");
        return new GeneratedFile("run-monolith.sh", Lf(text));
    }

    private static GeneratedFile HelmValues(SolutionManifest manifest)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Helm values sinh bởi tools/solution-cli từ solution.yaml. Không sửa tay.")
            .AppendLine("solution:")
            .AppendLine(CultureInfo.InvariantCulture, $"  name: {manifest.Solution.Name}")
            .AppendLine(CultureInfo.InvariantCulture, $"  version: \"{manifest.Solution.Version}\"")
            .AppendLine("apps:");
        foreach (var app in manifest.Apps)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - name: {app.Service}")
                .AppendLine(CultureInfo.InvariantCulture, $"    image: {app.Image}")
                .AppendLine(CultureInfo.InvariantCulture, $"    tag: \"{manifest.Solution.Version}\"")
                .AppendLine(CultureInfo.InvariantCulture, $"    replicas: {app.Replicas}")
                .AppendLine(CultureInfo.InvariantCulture, $"    port: {app.Port}")
                .AppendLine("    functionalBlocks:");
            foreach (var block in app.FunctionalBlocks)
            { text.AppendLine(CultureInfo.InvariantCulture, $"      {block.Name}: \"{block.Version}\""); }
            if (app.PublicObjectModel is { } pom)
            { text.AppendLine(CultureInfo.InvariantCulture, $"    publicObjectModel: {{ route: {pom.Route}, odataVersion: \"{pom.OdataVersion}\" }}"); }
        }
        text.AppendLine("workers:");
        foreach (var worker in manifest.Workers)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - name: {worker.Service}")
                .AppendLine(CultureInfo.InvariantCulture, $"    image: {worker.Image}")
                .AppendLine(CultureInfo.InvariantCulture, $"    tag: \"{manifest.Solution.Version}\"")
                .AppendLine(CultureInfo.InvariantCulture, $"    replicas: {worker.Replicas}");
        }
        return new GeneratedFile("values.yaml", Lf(text));
    }

    /// <summary>File sinh ra dùng LF trên mọi OS: shell script CRLF không chạy được trong container Linux.</summary>
    private static string Lf(StringBuilder text) => text.ToString().ReplaceLineEndings("\n");
}
