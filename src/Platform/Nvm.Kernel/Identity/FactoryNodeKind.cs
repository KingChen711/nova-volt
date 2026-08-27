namespace Nvm.Kernel.Identity;

/// <summary>
/// A level of the ISA-95 equipment hierarchy. The numeric value <b>is</b> the depth in an
/// <see cref="EquipmentPath"/>.
/// </summary>
/// <remarks>
/// <para>
/// Six levels, fixed by docs/scope.md §2.1. Each answers a different question, asked by a different
/// person: <see cref="Area"/> is what a shift supervisor watches, <see cref="Equipment"/> is what
/// traceability needs, and <see cref="Site"/> is where the security boundary runs.
/// </para>
/// <para>
/// Values are assigned explicitly because they carry meaning. A path with four segments describes a
/// line, and nothing else; that correspondence is what makes it impossible to hang a piece of
/// equipment straight off a site.
/// </para>
/// </remarks>
public enum FactoryNodeKind
{
    /// <summary>The company. One segment: <c>NOVAVOLT</c>.</summary>
    Enterprise = 1,

    /// <summary>A plant. Two segments: <c>NOVAVOLT/NV1</c>. The boundary authorization runs on.</summary>
    Site = 2,

    /// <summary>A process area. Three segments: <c>NOVAVOLT/NV1/FORMATION</c>.</summary>
    Area = 3,

    /// <summary>A production line. Four segments: <c>NOVAVOLT/NV1/FORMATION/F1</c>.</summary>
    Line = 4,

    /// <summary>A station or machine. Five segments: <c>…/F1/FORM-01</c>.</summary>
    WorkCell = 5,

    /// <summary>
    /// The finest level traceability records. Six segments: <c>…/FORM-01/FORM-01-CH-0142</c>, one
    /// charging channel out of a thousand on a formation machine.
    /// </summary>
    Equipment = 6,
}
