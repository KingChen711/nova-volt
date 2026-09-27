using Nvm.SolutionCli;

// Nvm.SolutionCli validate | matrix | generate --mode monolith|distributed --out <dir>   [--root <repo>]
var root = Option("--root") ?? FindRoot(Directory.GetCurrentDirectory());
var manifest = SolutionManifest.Parse(await File.ReadAllTextAsync(Path.Combine(root, "solution.yaml")));
var blocks = BlockCatalog.Load(root);
var errors = SolutionValidator.Validate(manifest, blocks);
switch (args.FirstOrDefault())
{
    case "validate":
        foreach (var error in errors)
        { await Console.Error.WriteLineAsync("ERROR " + error); }
        await Console.Out.WriteLineAsync(errors.Count == 0 ? $"OK {manifest.Solution.Name} {manifest.Solution.Version}" : $"{errors.Count} lỗi");
        return errors.Count == 0 ? 0 : 1;
    case "matrix":
        await Console.Out.WriteAsync(SolutionValidator.Matrix(manifest, blocks));
        return 0;
    case "generate":
        if (errors.Count > 0)
        {
            // Không sinh cấu hình từ manifest sai: file sinh ra trông hợp lệ và sẽ được deploy.
            foreach (var error in errors)
            { await Console.Error.WriteLineAsync("ERROR " + error); }
            return 1;
        }
        var mode = Option("--mode") ?? manifest.Solution.Mode;
        var output = Option("--out") ?? Path.Combine(root, "artifacts", "solution", mode);
        Directory.CreateDirectory(output);
        foreach (var file in SolutionGenerator.Generate(manifest, mode))
        {
            await File.WriteAllTextAsync(Path.Combine(output, file.Path), file.Content);
            await Console.Out.WriteLineAsync(Path.Combine(output, file.Path));
        }
        return 0;
    default:
        await Console.Error.WriteLineAsync("Usage: Nvm.SolutionCli validate | matrix | generate [--mode monolith|distributed] [--out dir] [--root repo]");
        return 2;
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string FindRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "solution.yaml")))
        { return directory.FullName; }
    }
    throw new FileNotFoundException("solution.yaml not found above the current directory.");
}
