namespace Nvm.Host.Infrastructure;

/// <summary>Finds <c>deploy/seed/factory-model.json</c> without anyone having to type a path.</summary>
/// <remarks>
/// <para>
/// The seed is read from disk rather than embedded (M1/C07) so that a plant change is a file edit
/// and not a rebuild. That leaves the question of where the file is, and the answer differs between
/// <c>dotnet run</c> from the project directory and a binary started out of <c>artifacts/bin</c>.
/// </para>
/// <para>
/// So the same upward walk <c>DotEnvLoader</c> uses: start where the process is and climb until the
/// repository turns up. One rule, one behaviour, and nothing to configure until there is a real
/// deployment — where <c>NVM_SEED_FILE</c> takes over and the walk never runs.
/// </para>
/// </remarks>
internal static class SeedFileLocator
{
    /// <summary>Environment variable that names the file outright.</summary>
    public const string PathVariable = "NVM_SEED_FILE";

    private const string RelativePath = "deploy/seed/factory-model.json";

    /// <summary>Locates the seed file, starting from where the process was launched.</summary>
    /// <param name="contentRootPath">Directory to start searching upward from.</param>
    /// <exception cref="FileNotFoundException">No ancestor directory holds the seed file.</exception>
    public static string Locate(string contentRootPath)
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured)
        {
            return configured;
        }

        var directory = new DirectoryInfo(contentRootPath);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"No '{RelativePath}' found in '{contentRootPath}' or any directory above it. "
            + $"Set {PathVariable} to name the file directly.",
            RelativePath);
    }
}
