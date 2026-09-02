using System.Collections.Immutable;
using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;
using Nvm.Simulator;

namespace Nvm.TelemetryBackfill;

/// <summary>Chọn channel từ cùng factory-model catalogue mà online simulator sử dụng.</summary>
public static class BackfillTopology
{
    /// <summary>Load revision mới nhất và lấy các channel được yêu cầu theo thứ tự path ổn định.</summary>
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
