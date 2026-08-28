namespace Nvm.Kernel.Identity;

/// <summary>Answers whether a place in the plant exists, and where a machine code sits.</summary>
/// <remarks>
/// <para>
/// Declared here rather than in the Functional Block that answers it, because the callers are not
/// Functional Blocks. Ingestion and the edge gateway need to turn a code stencilled on a machine into
/// an <see cref="EquipmentPath"/>, and K8 does not let them reach into <c>Nvm.FactoryModel</c> to do
/// it. The block implements this; Platform code depends on the question, not on the answer.
/// </para>
/// <para>
/// It reports what is in force <b>now</b>, which is the right answer for a message that just arrived
/// off the shop floor and the wrong one for reading history. A work cell scrapped last year is
/// absent here and still named by every traceability record written while it existed — so callers
/// interpreting old records must go to the revision that was in force then, not to this.
/// </para>
/// </remarks>
public interface IEquipmentDirectory
{
    /// <summary>Whether the plant currently has a node at this exact path.</summary>
    /// <param name="path">The path to look for.</param>
    bool Contains(EquipmentPath path);

    /// <summary>Finds the machine known by <paramref name="deviceCode"/> on a given line.</summary>
    /// <param name="line">The line the device reports under.</param>
    /// <param name="deviceCode">The code the device calls itself, for example <c>FORM-01-CH-0142</c>.</param>
    /// <returns>The full path, or null when the line has no such device.</returns>
    /// <remarks>
    /// <para>
    /// Two levels are searched, and that is the whole reason this cannot be done with string
    /// concatenation. A device on the wire is sometimes a work cell — <c>STACK-01</c> hangs straight
    /// off line <c>L1</c> — and sometimes a piece of equipment inside one, as
    /// <c>FORM-01-CH-0142</c> does inside <c>FORM-01</c>. The topic carries no hint of which, so the
    /// model has to be asked.
    /// </para>
    /// <para>
    /// Scoped to a line rather than to a plant because codes repeat: NV1 and DE1 both have an
    /// <c>MLOAD-01</c>, and a plant-wide code lookup would answer with whichever it indexed last.
    /// </para>
    /// </remarks>
    EquipmentPath? FindDevice(EquipmentPath line, string deviceCode);
}
