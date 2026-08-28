using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;

namespace Nvm.EdgeGateway;

/// <summary>Builds the gateway's read-only view of the published factory model.</summary>
internal static class GatewayEquipmentDirectory
{
    /// <summary>Loads one revision and activates its site trees in this process.</summary>
    internal static IEquipmentDirectory Load(string seedDirectory, int? requestedRevision) =>
        SeededEquipmentDirectory.Load(seedDirectory, requestedRevision);
}
