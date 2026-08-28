namespace Nvm.Kernel.Identity;

/// <summary>Reads the process step a machine performs out of its code.</summary>
/// <remarks>
/// <para>
/// <c>FORM-01</c> is formation cycler 1, <c>STACK-02</c> is stacker 2, <c>AGE-RACK-01</c> is an aging
/// rack. The step is the part before the first hyphen, and that is a <b>naming convention of the
/// plant</b>, not a fact the model states — every work cell in
/// <c>deploy/seed/factory-model.r*.json</c> follows it, and nothing enforces that it always will.
/// </para>
/// <para>
/// It is relied on here because M2 has no routing data yet. The real answer comes from the routing of
/// the product being made, which is a table in the database and arrives with M3; until then this is
/// the only way to say which step a reading belongs to without inventing a lookup nobody maintains.
/// </para>
/// <para>
/// The consequence of it being wrong is smaller than it looks, and worth knowing before the
/// convention changes. Its job in a natural key is to keep two different facts apart
/// (docs/scope.md §7.2), and a value that is derived <i>consistently</i> does that whether or not it
/// is the name a process engineer would use. It is not stored — <c>ts.process_signal</c> has no
/// <c>step_code</c> column — so a wrong value pollutes nothing. What would break is a key derived one
/// way today and another way tomorrow, which is why this lives in one place.
/// </para>
/// </remarks>
public static class ProcessStepCode
{
    private const char CodeSeparator = '-';

    /// <summary>The step a path performs, or null when the path names no machine.</summary>
    /// <param name="path">A work cell, or equipment inside one.</param>
    /// <returns>
    /// <c>FORM</c> for <c>NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142</c>; null for anything
    /// shallower than a work cell.
    /// </returns>
    /// <remarks>
    /// Taken from the <b>work cell</b>, not from the deepest segment. A formation channel is called
    /// <c>FORM-01-CH-0142</c> and would give the same answer by accident; an aging rack position
    /// would not. The cell is the machine that performs the step, and its children are parts of it.
    /// </remarks>
    public static string? FromEquipmentPath(EquipmentPath? path)
    {
        if (path is null || path.Kind < FactoryNodeKind.WorkCell)
        {
            return null;
        }

        var workCell = path.Segments[(int)FactoryNodeKind.WorkCell - 1];
        var separator = workCell.IndexOf(CodeSeparator, StringComparison.Ordinal);

        return separator <= 0 ? workCell : workCell[..separator];
    }
}
