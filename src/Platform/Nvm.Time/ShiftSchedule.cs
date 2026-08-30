using System.Collections.Immutable;
using System.Globalization;

namespace Nvm.Time;

/// <summary>A plant's shift table, checked to cover the clock exactly once.</summary>
/// <remarks>
/// <para>
/// <b>Data, not a switch-case.</b> docs/scope.md §2.3 gives NovaVolt one table, and M10 brings a site
/// that has a different one. A table can be replaced per site; a <c>switch</c> on
/// <see cref="Shift"/> spread through the codebase cannot, and the day someone tries they find the
/// hours written down in four places that have quietly drifted apart.
/// </para>
/// <para>
/// The construction check is the whole value of the type: a table that leaves 05:00 uncovered would
/// give a measurement taken at 05:00 no shift at all, and one that covers 05:00 twice would give it
/// two. Both are found here rather than in a report six months later.
/// </para>
/// <para>
/// <b>Order carries meaning.</b> The rows are kept as they were given, because the first one opens the
/// production day — that is what makes 06:00 the boundary rather than midnight. Sorting them would
/// throw that away for a table whose day starts at 22:00.
/// </para>
/// </remarks>
public sealed class ShiftSchedule
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private readonly ImmutableArray<ShiftDefinition> _definitions;

    private ShiftSchedule(ImmutableArray<ShiftDefinition> definitions)
    {
        _definitions = definitions;
        DayStart = definitions[0].LocalStart;
    }

    /// <summary>The NovaVolt table: A 06–14, B 14–22, C 22–06 local (docs/scope.md §2.3).</summary>
    public static ShiftSchedule Default { get; } = Create(
    [
        new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
        new ShiftDefinition(Shift.B, new TimeOnly(14, 0), TimeSpan.FromHours(8)),
        new ShiftDefinition(Shift.C, new TimeOnly(22, 0), TimeSpan.FromHours(8)),
    ]);

    /// <summary>The rows, in running order, starting with the one that opens the production day.</summary>
    /// <remarks>
    /// <see cref="ImmutableArray{T}"/> rather than <c>IReadOnlyList</c> — <c>ADR-025</c>. A caller that
    /// could cast this back to an array could reorder a table whose invariants were checked once, at
    /// construction, and never again.
    /// </remarks>
    public ImmutableArray<ShiftDefinition> Definitions => _definitions;

    /// <summary>The wall clock reading a production day begins at. 06:00 for NovaVolt.</summary>
    /// <remarks>
    /// Read off the first row rather than written down a second time: a site whose opening shift
    /// starts at 07:00 has a production day starting at 07:00, and nobody should have to remember to
    /// change a constant to say so.
    /// </remarks>
    public TimeOnly DayStart { get; }

    /// <summary>Builds a shift table, refusing one that does not cover the clock exactly once.</summary>
    /// <param name="definitions">
    /// The rows in running order, opening shift first. Any starting point is accepted as long as the
    /// table is contiguous when walked from the first row.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The table is empty, names a shift twice, is not contiguous, or does not add up to 24 hours.
    /// </exception>
    public static ShiftSchedule Create(IEnumerable<ShiftDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var rows = definitions.ToImmutableArray();

        if (rows.IsEmpty)
        {
            throw new ArgumentException("A shift table needs at least one shift.", nameof(definitions));
        }

        if (rows.Select(row => row.Shift).Distinct().Count() != rows.Length)
        {
            throw new ArgumentException("A shift table names each shift once.", nameof(definitions));
        }

        var total = TimeSpan.Zero;

        foreach (var row in rows)
        {
            if (row.NominalLength <= TimeSpan.Zero || row.NominalLength > Day)
            {
                throw new ArgumentException(
                    $"Shift {row.Shift} lasts {row.NominalLength}, which is not a length a shift can have.",
                    nameof(definitions));
            }

            total += row.NominalLength;
        }

        if (total != Day)
        {
            throw new ArgumentException(
                $"The shift table covers {total} of the clock. It has to cover exactly 24 hours: an "
                + "uncovered minute is a measurement that belongs to no shift, and a doubly covered one "
                + "is a measurement that belongs to two.",
                nameof(definitions));
        }

        // Walked with a wrap, so the shift that crosses midnight is not a special case here — it is
        // simply the row whose successor is the first row again. Together with the 24-hour total,
        // contiguity is what rules out both a gap and an overlap.
        for (var index = 0; index < rows.Length; index++)
        {
            var current = rows[index];
            var next = rows[(index + 1) % rows.Length];
            var expected = current.LocalStart.Add(current.NominalLength);

            if (expected != next.LocalStart)
            {
                throw new ArgumentException(
                    $"Shift {current.Shift} ends at {Format(expected)} but shift {next.Shift} starts at "
                    + $"{Format(next.LocalStart)}. The table has to be contiguous.",
                    nameof(definitions));
            }
        }

        return new ShiftSchedule(rows);
    }

    /// <summary>The shift covering a wall clock reading.</summary>
    /// <param name="localTimeOfDay">A reading of the site's clock, with no date and no offset.</param>
    /// <remarks>
    /// Total by construction: the table was checked to cover the clock exactly once, so there is
    /// always an answer and never two.
    /// </remarks>
    public ShiftDefinition ShiftAt(TimeOnly localTimeOfDay)
    {
        // TimeOnly subtraction wraps at midnight and never comes back negative, which is what lets the
        // shift crossing midnight fall out of the same arithmetic as the other two.
        var sinceDayStart = localTimeOfDay - DayStart;
        var elapsed = TimeSpan.Zero;

        foreach (var definition in _definitions)
        {
            elapsed += definition.NominalLength;

            if (sinceDayStart < elapsed)
            {
                return definition;
            }
        }

        // Unreachable while the construction check holds. Throwing rather than returning the last row
        // means a future change that breaks the invariant is found here, instead of quietly filing the
        // small hours under the wrong shift.
        throw new InvalidOperationException(
            $"No shift covers {Format(localTimeOfDay)}, which a validated table cannot happen to.");
    }

    /// <summary>The row for one shift.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The table does not run that shift.</exception>
    public ShiftDefinition Definition(Shift shift)
    {
        foreach (var definition in _definitions)
        {
            if (definition.Shift == shift)
            {
                return definition;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(shift), shift, "This plant does not run that shift.");
    }

    /// <summary>How far into the production day a shift starts.</summary>
    /// <param name="shift">The shift.</param>
    /// <remarks>
    /// Zero for the opening shift, sixteen hours for shift C. This is the number that turns "shift C of
    /// production day 25" into a wall clock reading on a calendar date, and it is derived from the
    /// table rather than assumed to be a multiple of eight hours.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The table does not run that shift.</exception>
    public TimeSpan OffsetIntoDay(Shift shift)
    {
        var elapsed = TimeSpan.Zero;

        foreach (var definition in _definitions)
        {
            if (definition.Shift == shift)
            {
                return elapsed;
            }

            elapsed += definition.NominalLength;
        }

        throw new ArgumentOutOfRangeException(nameof(shift), shift, "This plant does not run that shift.");
    }

    private static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
