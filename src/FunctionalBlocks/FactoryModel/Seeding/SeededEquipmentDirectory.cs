using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Seeding;

/// <summary>Builds a read-only view of one published revision of the factory model.</summary>
/// <remarks>
/// Shared by every process that has to answer "does the plant actually have this machine" without
/// owning the model: the edge gateway resolving a Sparkplug topic, ingestion resolving an equipment
/// path off a CSV line. Two copies of this loader would be two places for the revision-selection
/// rule to drift, and a gateway and an ingestion that disagree about which revision is active accept
/// and refuse different messages for the same plant.
/// </remarks>
public static class SeededEquipmentDirectory
{
    /// <summary>Loads one revision and activates its site trees in this process.</summary>
    /// <param name="seedDirectory">Directory holding the <c>factory-model.r*.json</c> documents.</param>
    /// <param name="requestedRevision">The revision to load, or null for the newest published.</param>
    /// <exception cref="InvalidOperationException">The catalog has no such revision.</exception>
    public static IEquipmentDirectory Load(string seedDirectory, int? requestedRevision)
    {
        var catalog = FactoryModelSeed.LoadCatalog(seedDirectory);
        var revision = requestedRevision ?? catalog.LatestRevision;
        var snapshot = catalog.Find(revision)
            ?? throw new InvalidOperationException(
                $"Revision {revision} is not on the shelf. The catalog holds {string.Join(", ", catalog.Revisions)}.");

        var active = new InMemoryActiveFactoryModel();

        foreach (FactorySite site in snapshot.Sites)
        {
            if (!active.TryActivate(new ActiveFactoryModelRevision(snapshot.Revision, site), expectedCurrentRevision: null))
            {
                throw new InvalidOperationException(
                    $"Site '{site.SiteId}' was activated twice while loading revision {snapshot.Revision}.");
            }
        }

        return new FactoryModelEquipmentDirectory(active);
    }
}
