using System.Globalization;
using System.Text.Json;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Seeding;

/// <summary>Reads the factory model documents and turns them into validated snapshots.</summary>
/// <remarks>
/// <para>
/// Read from disk rather than embedded in the assembly, so the plant can be corrected without a
/// rebuild — and so the file stays something an engineer can be shown, edited, and diffed in a review.
/// </para>
/// <para>
/// <b>One file per revision</b>, named <c>factory-model.r2.json</c>. A revision is a document and
/// documents are not edited (<see cref="IFactoryModelCatalog"/>), so a new revision is a new file
/// next to the old one rather than a change to it. The directory is the whole shelf.
/// </para>
/// </remarks>
public static class FactoryModelSeed
{
    /// <summary>What every revision file name starts with.</summary>
    public const string FileNamePrefix = "factory-model.r";

    /// <summary>What every revision file name ends with.</summary>
    public const string FileNameSuffix = ".json";

    /// <summary>The file name a given revision is expected to have.</summary>
    /// <param name="revision">The revision number, at least 1.</param>
    public static string FileNameFor(int revision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);

        return FileNamePrefix + revision.ToString(CultureInfo.InvariantCulture) + FileNameSuffix;
    }

    /// <summary>Reads every revision document in a directory.</summary>
    /// <param name="directoryPath">Directory holding the <c>factory-model.r*.json</c> files.</param>
    /// <exception cref="DirectoryNotFoundException">The directory is not there.</exception>
    /// <exception cref="FactoryModelSeedException">
    /// The directory holds no document, or one of them is not a valid plant, or a file name and the
    /// revision inside it disagree.
    /// </exception>
    /// <remarks>
    /// Every problem here is fatal at startup, and none of the files is skipped quietly. A document
    /// silently ignored is a published rollout that vanished, and nobody finds out until the day an
    /// operator activates a revision the catalog never loaded.
    /// </remarks>
    public static InMemoryFactoryModelCatalog LoadCatalog(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Factory model seed directory not found: '{directoryPath}'. "
                + $"It must hold at least '{FileNameFor(1)}'.");
        }

        var files = Directory.GetFiles(directoryPath, FileNamePrefix + "*" + FileNameSuffix);

        if (files.Length == 0)
        {
            throw new FactoryModelSeedException(
                $"No factory model document in '{directoryPath}'. "
                + $"Revision files are named '{FileNameFor(1)}'.");
        }

        var snapshots = new List<FactoryModelSnapshot>(files.Length);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var revisionText = name[FileNamePrefix.Length..^FileNameSuffix.Length];

            if (!int.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            {
                throw new FactoryModelSeedException(
                    $"'{name}' is not a revision file name. "
                    + $"Expected '{FileNamePrefix}<number>{FileNameSuffix}'.");
            }

            var snapshot = Load(file);

            // The name and the content have to agree. Copying r2 to r3 and forgetting to change the
            // number inside is the easiest mistake to make here, and it would put the old tree in
            // force under a new revision number — a change everybody believes happened and did not.
            if (snapshot.Revision != revision)
            {
                throw new FactoryModelSeedException(
                    $"'{name}' holds revision {snapshot.Revision}. "
                    + "The file name and the document must name the same revision.");
            }

            snapshots.Add(snapshot);
        }

        return new InMemoryFactoryModelCatalog(snapshots);
    }

    /// <summary>Reads and validates a single revision document.</summary>
    /// <param name="filePath">Full path to the JSON file.</param>
    /// <exception cref="FileNotFoundException">The file is not there.</exception>
    /// <exception cref="FactoryModelSeedException">The file is there and does not describe a valid plant.</exception>
    public static FactoryModelSnapshot Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"Factory model document not found. Expected a file named '{FileNameFor(1)}' or "
                + "another revision at this path.",
                filePath);
        }

        return Parse(File.ReadAllText(filePath));
    }

    /// <summary>Validates seed content that has already been read.</summary>
    /// <param name="json">The file's contents.</param>
    /// <exception cref="FactoryModelSeedException">The content does not describe a valid plant.</exception>
    public static FactoryModelSnapshot Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        FactoryModelSeedDocument? document;

        try
        {
            document = JsonSerializer.Deserialize(json, FactoryModelSeedJsonContext.Default.FactoryModelSeedDocument);
        }
        catch (JsonException failure)
        {
            throw new FactoryModelSeedException("The factory model seed is not valid JSON.", failure);
        }

        if (document is null)
        {
            throw new FactoryModelSeedException("The factory model seed is empty.");
        }

        // Revision 0 is what an uninitialised integer looks like, and a revision that never moves is
        // indistinguishable from a plant that never changed.
        if (document.Revision < 1)
        {
            throw new FactoryModelSeedException(
                $"Revision must be at least 1, found {document.Revision}.");
        }

        if (!EquipmentPath.TryParse(document.Enterprise.Code, out var rootPath))
        {
            throw new FactoryModelSeedException(
                $"'{document.Enterprise.Code}' is not a valid enterprise code. Codes are upper case.");
        }

        var root = BuildNode(document.Enterprise, rootPath);

        return new FactoryModelSnapshot(document.Revision, document.GeneratedAt, root, CollectSites(root, document));
    }

    private static FactoryNode BuildNode(FactoryNodeSeed seed, EquipmentPath path)
    {
        // A time zone belongs to a plant and to nothing else. Finding one on a work cell means either
        // the file is wrong or somebody is about to start reading it from the wrong level.
        var isSite = path.Kind == FactoryNodeKind.Site;

        if (isSite && string.IsNullOrWhiteSpace(seed.TimeZoneId))
        {
            throw new FactoryModelSeedException($"Site '{path}' has no timeZoneId.");
        }

        if (!isSite && seed.TimeZoneId is not null)
        {
            throw new FactoryModelSeedException(
                $"'{path}' is a {path.Kind} and carries a timeZoneId. Only a {FactoryNodeKind.Site} has one.");
        }

        var children = new List<FactoryNode>();

        foreach (var child in seed.Children ?? [])
        {
            EquipmentPath childPath;

            try
            {
                childPath = path.Append(child.Code);
            }
            catch (Exception failure) when (failure is FormatException or InvalidOperationException)
            {
                throw new FactoryModelSeedException(
                    $"'{child.Code}' cannot sit under '{path}': {failure.Message}",
                    failure);
            }

            children.Add(BuildNode(child, childPath));
        }

        try
        {
            return FactoryNode.Create(path, seed.Name, children);
        }
        catch (ArgumentException failure)
        {
            throw new FactoryModelSeedException($"'{path}' is not a valid node: {failure.Message}", failure);
        }
    }

    private static List<FactorySite> CollectSites(FactoryNode root, FactoryModelSeedDocument document)
    {
        var timeZones = (document.Enterprise.Children ?? [])
            .ToDictionary(site => site.Code, site => site.TimeZoneId!, StringComparer.Ordinal);

        return [.. root.Children.Select(node =>
            new FactorySite(node.Code, node.Name, timeZones[node.Code], node))];
    }
}
