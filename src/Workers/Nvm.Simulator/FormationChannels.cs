using System.Collections.Immutable;
using Nvm.FactoryModel.Entities;
using Nvm.Kernel.Identity;

namespace Nvm.Simulator;

/// <summary>Reads the channel list off the plant instead of inventing one.</summary>
/// <remarks>
/// The simulator is not free to make up equipment. A topic naming a device the plant does not have is
/// refused by ingestion (K3), and a run whose every message is refused measures nothing — the failure
/// would look like a broken pipeline rather than a made-up channel list.
/// </remarks>
public static class FormationChannels
{
    /// <summary>Every channel under a line, in path order.</summary>
    /// <param name="model">The revision the plant is running.</param>
    /// <param name="linePath">The line to look under.</param>
    /// <exception cref="InvalidOperationException">The revision has no channels there.</exception>
    /// <remarks>
    /// Equipment level only. A work cell with nothing under it — <c>FORM-02</c> in the seed, a cycler
    /// whose channels have not been modelled — is a machine, not a charging channel, and simulating it
    /// as one would put formation readings on a device that has none.
    /// </remarks>
    public static ImmutableArray<EquipmentPath> Under(FactoryModelSnapshot model, EquipmentPath linePath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(linePath);

        var prefix = linePath.Value + EquipmentPath.Separator;

        var channels = model.Paths
            .Where(path => path.Kind == FactoryNodeKind.Equipment
                && path.Value.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(path => path.Value, StringComparer.Ordinal)
            .ToImmutableArray();

        return channels.IsEmpty
            ? throw new InvalidOperationException(
                $"Revision {model.Revision} has no equipment under '{linePath.Value}'. Either the line "
                + "is wrong or this revision does not model its channels yet.")
            : channels;
    }
}
