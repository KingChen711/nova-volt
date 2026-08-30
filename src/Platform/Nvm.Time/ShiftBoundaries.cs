namespace Nvm.Time;

/// <summary>When one shift of one production day actually started and ended.</summary>
/// <param name="Day">The production day the shift belongs to.</param>
/// <param name="Shift">Which shift.</param>
/// <param name="Start">The instant it started, inclusive.</param>
/// <param name="End">The instant it ended, exclusive.</param>
/// <remarks>
/// <para>
/// <b>Two absolute instants, not two clock readings.</b> "22:00 to 06:00" is what the shift board on
/// the wall says; it is not enough to select rows with, because on two days a year at DE1 those two
/// readings are seven hours apart and on two others they are nine. Everything that has to count
/// something over a shift uses these.
/// </para>
/// <para>
/// <b>Half-open, <c>[Start, End)</c>.</b> A closed interval would give the instant at 14:00 to both
/// shift A and shift B, and a measurement counted in two shifts is a measurement counted twice —
/// small enough to look like noise, large enough to move a yield figure. The end of one shift is
/// exactly the start of the next, by construction.
/// </para>
/// </remarks>
public readonly record struct ShiftBoundaries(
    ProductionDay Day,
    Shift Shift,
    DateTimeOffset Start,
    DateTimeOffset End)
{
    /// <summary>How long the shift really lasted.</summary>
    /// <remarks>
    /// Eight hours on 363 days a year at DE1, seven on one and nine on one. That is not an error to
    /// correct — it is the fact an OEE calculation dividing by a hard-coded eight hours gets wrong,
    /// and then reports as a 12,5 % efficiency drop that never happened.
    /// </remarks>
    public TimeSpan Duration => End - Start;

    /// <summary>Whether an instant falls in this shift.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;
}
