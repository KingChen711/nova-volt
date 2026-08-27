using System.Text.Json;
using Nvm.FactoryModel.Entities;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Seeding;

/// <summary>Reads <c>factory-model.json</c> and turns it into a validated snapshot.</summary>
/// <remarks>
/// Read from disk rather than embedded in the assembly, so the plant can be corrected without a
/// rebuild — and so the file stays something an engineer can be shown, edited, and diffed in a review.
/// </remarks>
public static class FactoryModelSeed
{
    /// <summary>The file name expected in the seed directory.</summary>
    public const string FileName = "factory-model.json";

    /// <summary>Reads and validates the seed file at the given path.</summary>
    /// <param name="filePath">Full path to the JSON file.</param>
    /// <exception cref="FileNotFoundException">The file is not there.</exception>
    /// <exception cref="FactoryModelSeedException">The file is there and does not describe a valid plant.</exception>
    public static FactoryModelSnapshot Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"Factory model seed not found. Expected '{FileName}' at this path.",
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
