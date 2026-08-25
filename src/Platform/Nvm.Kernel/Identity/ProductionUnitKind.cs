namespace Nvm.Kernel.Identity;

/// <summary>
/// The kind of serialized production unit, encoded as one character in a <see cref="SerialNumber"/>.
/// </summary>
/// <remarks>
/// Product NV-C100-LFP-CTP has no module tier, so a plant may never produce a
/// <see cref="Module"/>. The genealogy graph decides which tiers exist, not this enum.
/// </remarks>
public enum ProductionUnitKind
{
    /// <summary>Smallest serialized unit. Engraved code carries 'C'.</summary>
    Cell,

    /// <summary>Group of cells with a frame and busbars. Engraved code carries 'M'.</summary>
    Module,

    /// <summary>Deliverable assembly containing modules or, for cell-to-pack, cells. Engraved code carries 'P'.</summary>
    Pack,
}
