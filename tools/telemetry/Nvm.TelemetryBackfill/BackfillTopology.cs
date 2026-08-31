using System.Collections.Immutable;
using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;
using Nvm.Simulator;

namespace Nvm.TelemetryBackfill;

/// <summary>Selects channels from the same factory-model catalogue the online simulator uses.</summary>
public static class BackfillTopology
{
    /// <summary>Loads the latest revision and takes the requested channels in stable path order.</summary>
    public static ImmutableArray<EquipmentPath> Load(
        string seedDirectory,
        EquipmentPath linePath,
        int channelCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedDirectory);
        ArgumentNullException.ThrowIfNull(linePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);

        var catalog = FactoryModelSeed.LoadCatalog(seedDirectory);
        var model = catalog.Find(catalog.LatestRevision)
            ?? throw new InvalidOperationException($"'{seedDirectory}' has no factory-model revision.");
        var available = FormationChannels.Under(model, linePath);

        if (channelCount > available.Length)
        {
            throw new InvalidOperationException(
                $"Requested {channelCount} channels, but revision {model.Revision} has {available.Length} "
                + $"under '{linePath.Value}'.");
        }

        return [.. available.Take(channelCount)];
    }
}
