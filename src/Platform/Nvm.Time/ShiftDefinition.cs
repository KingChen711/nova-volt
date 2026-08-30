namespace Nvm.Time;

/// <summary>One row of a plant's shift table: when a shift starts and how long it nominally runs.</summary>
/// <param name="Shift">Which shift this row describes.</param>
/// <param name="LocalStart">The wall clock reading the shift starts at, in the site's own time.</param>
/// <param name="NominalLength">
/// How long the shift lasts <b>on the clock</b> — eight hours for every shift here.
/// </param>
/// <remarks>
/// <para>
/// <b>The word "nominal" is load-bearing.</b> This is a wall-clock length, not an elapsed one. On the
/// two days a year DE1 changes its clocks, shift C runs for a nominal eight hours and an actual seven
/// or nine, and the difference is not an error in either number: the shift really does start at 22:00
/// and really does end at 06:00, and the clock really did skip an hour in between. Anything that
/// needs the elapsed length must ask the production calendar for the shift's boundaries and
/// subtract them, never multiply this by anything.
/// </para>
/// <para>
/// <see cref="TimeOnly"/> rather than <see cref="DateTimeOffset"/> on purpose: a shift table is a
/// statement about clock readings that repeats every day, and it has no offset because the offset is
/// a property of the site and the date, not of the table.
/// </para>
/// </remarks>
public readonly record struct ShiftDefinition(Shift Shift, TimeOnly LocalStart, TimeSpan NominalLength);
