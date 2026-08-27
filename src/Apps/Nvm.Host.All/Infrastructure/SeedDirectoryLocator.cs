using Nvm.FactoryModel.Seeding;

namespace Nvm.Host.Infrastructure;

/// <summary>Finds <c>deploy/seed</c> without anyone having to type a path.</summary>
/// <remarks>
/// <para>
/// The model is read from disk rather than embedded (M1/C07) so that a plant change is a file added
/// and not a rebuild. That leaves the question of where the files are, and the answer differs between
/// <c>dotnet run</c> from the project directory and a binary started out of <c>artifacts/bin</c>.
/// </para>
/// <para>
/// So the same upward walk <c>DotEnvLoader</c> uses: start where the process is and climb until the
/// repository turns up. One rule, one behaviour, and nothing to configure until there is a real
/// deployment — where <c>NVM_SEED_DIR</c> takes over and the walk never runs.
/// </para>
/// <para>
/// A directory rather than a file, because a revision is a document and the plant has more than one
/// (<see cref="Nvm.FactoryModel.Storage.IFactoryModelCatalog"/>). The walk looks for a directory that
/// actually holds a revision document, not merely one named <c>seed</c>: an empty directory further
/// down the tree would otherwise shadow the real one and the process would die reporting the wrong
/// cause.
/// </para>
/// </remarks>
internal static class SeedDirectoryLocator
{
    /// <summary>Environment variable that names the directory outright.</summary>
    public const string PathVariable = "NVM_SEED_DIR";

    private const string RelativePath = "deploy/seed";

    /// <summary>Locates the seed directory, starting from where the process was launched.</summary>
    /// <param name="contentRootPath">Directory to start searching upward from.</param>
    /// <exception cref="DirectoryNotFoundException">No ancestor directory holds the seed documents.</exception>
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

            if (HoldsARevision(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No '{RelativePath}' holding '{FactoryModelSeed.FileNameFor(1)}' found in "
            + $"'{contentRootPath}' or any directory above it. "
            + $"Set {PathVariable} to name the directory directly.");
    }

    private static bool HoldsARevision(string candidate) =>
        Directory.Exists(candidate)
        && Directory
            .EnumerateFiles(
                candidate,
                FactoryModelSeed.FileNamePrefix + "*" + FactoryModelSeed.FileNameSuffix)
            .Any();
}
