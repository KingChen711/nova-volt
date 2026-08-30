using System.Globalization;

namespace Nvm.Time;

/// <summary>The production cycle a measurement belongs to, named by a calendar date.</summary>
/// <remarks>
/// <para>
/// A production day is <b>not</b> a calendar day. It is the cycle that begins when shift A starts —
/// 06:00 local — and runs until shift A starts again. Shift C from 22:00 on the 25th to 06:00 on the
/// 26th belongs to production day <c>2026-08-25</c>, so six of its hours fall on a different calendar
/// date than the day it is filed under (docs/scope.md §2.3).
/// </para>
/// <para>
/// <b>Why a type rather than a <see cref="DateOnly"/>.</b> The two are structurally identical, and
/// that is precisely the danger: if a production day were a <see cref="DateOnly"/>, nothing would stop
/// someone assigning it the result of <c>CAST(device_timestamp AS date)</c> or of the local calendar
/// date, and the compiler would agree. Both are wrong for six hours out of every twenty-four, and the
/// error surfaces months later as "shift C looks short" in a monthly report. There is no implicit
/// conversion in either direction for exactly that reason — leaving the type is a deliberate call to
/// <see cref="Date"/>.
/// </para>
/// </remarks>
public readonly record struct ProductionDay : IComparable<ProductionDay>
{
    private ProductionDay(DateOnly date) => Date = date;

    /// <summary>The calendar date the cycle started on, in the site's local time.</summary>
    /// <remarks>
    /// Deliberately a property and not an implicit conversion. Reading it is a statement that the
    /// caller means the label of the cycle, not "the date this instant fell on".
    /// </remarks>
    public DateOnly Date { get; }

    /// <summary>Names a production day by the calendar date its shift A began on.</summary>
    public static ProductionDay On(DateOnly date) => new(date);

    /// <summary>Names a production day by year, month and day.</summary>
    public static ProductionDay On(int year, int month, int day) => new(new DateOnly(year, month, day));

    /// <summary>The production day that follows this one.</summary>
    public ProductionDay Next() => new(Date.AddDays(1));

    /// <summary>The production day before this one.</summary>
    public ProductionDay Previous() => new(Date.AddDays(-1));

    /// <summary>How many production days lie between this one and another.</summary>
    public int DaysUntil(ProductionDay other) => other.Date.DayNumber - Date.DayNumber;

    /// <inheritdoc />
    public int CompareTo(ProductionDay other) => Date.CompareTo(other.Date);

    /// <summary>Whether one production day is before another.</summary>
    public static bool operator <(ProductionDay left, ProductionDay right) => left.CompareTo(right) < 0;

    /// <summary>Whether one production day is after another.</summary>
    public static bool operator >(ProductionDay left, ProductionDay right) => left.CompareTo(right) > 0;

    /// <summary>Whether one production day is before another or the same.</summary>
    public static bool operator <=(ProductionDay left, ProductionDay right) => left.CompareTo(right) <= 0;

    /// <summary>Whether one production day is after another or the same.</summary>
    public static bool operator >=(ProductionDay left, ProductionDay right) => left.CompareTo(right) >= 0;

    /// <summary>The date, as <c>2026-08-25</c>.</summary>
    /// <remarks>
    /// Invariant and ISO, never the current culture: this string ends up in reports, log lines and
    /// SQL, and a machine in a <c>de-DE</c> locale writing <c>25.08.2026</c> would produce a second
    /// spelling of the same day.
    /// </remarks>
    public override string ToString() => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
