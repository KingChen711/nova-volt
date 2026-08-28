using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;

namespace Nvm.EdgeGateway;

/// <summary>Builds the gateway's read-only view of the published factory model.</summary>
internal static class GatewayEquipmentDirectory
{
    /// <summary>Loads one revision and activates its site trees in this process.</summary>
    internal static IEquipmentDirectory Load(string seedDirectory, int? requestedRevision)
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
                    $"Site '{site.SiteId}' was activated twice while the gateway loaded revision {snapshot.Revision}.");
            }
        }

        return new FactoryModelEquipmentDirectory(active);
    }
}
