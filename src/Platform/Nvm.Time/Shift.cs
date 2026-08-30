namespace Nvm.Time;

/// <summary>The three shifts a production day is made of.</summary>
/// <remarks>
/// <para>
/// The letters are the plant's own (docs/scope.md §2.3), not an invention: a supervisor says "ca C"
/// and an ERP report says <c>C</c>, so the enum spells it the way the shop floor does.
/// </para>
/// <para>
/// <see cref="C"/> is the only one that crosses midnight, and every awkward case in this assembly
/// comes from that single fact — a night shift belongs to the day it <b>started</b> on, which is the
/// day before the calendar date most of its hours fall in.
/// </para>
/// </remarks>
public enum Shift
{
    /// <summary>Morning shift, 06:00 to 14:00 local.</summary>
    A = 1,

    /// <summary>Afternoon shift, 14:00 to 22:00 local.</summary>
    B = 2,

    /// <summary>Night shift, 22:00 to 06:00 local the next calendar day.</summary>
    C = 3,
}
