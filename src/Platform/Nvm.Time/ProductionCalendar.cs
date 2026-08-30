namespace Nvm.Time;

/// <summary>The production calendar, computed in the plant's own local time.</summary>
/// <remarks>
/// <para>
/// <b>Everything happens in local time, and that is the whole design.</b> The tempting shortcut is to
/// subtract six hours on the UTC instant and take the date — it agrees with this class on 363 days a
/// year at DE1 and on every day at NV1, which is exactly what makes it dangerous. docs/scope.md §2.3
/// names it as the trap. A shift boundary is a statement about the clock on the wall, and on the two
/// days that clock is moved, an offset arithmetic that never looked at the zone puts part of shift C
/// on the wrong production day — and the neighbouring day is wrong by the same amount in the other
/// direction, so the totals still add up.
/// </para>
/// <para>
/// The one place local time is not enough is turning a shift boundary back into an instant, because
/// two clock readings a year are not instants at all: one hour never happens and one happens twice.
/// <see cref="Resolve"/> states the rule for both, and it is stated once so that the library's default
/// never gets to decide it silently.
/// </para>
/// </remarks>
public sealed class ProductionCalendar : IProductionCalendar
{
    // The wall-clock guess first, then a day either side of it. Ordered so the overwhelmingly common
    // case answers on the first candidate day instead of after searching the one before it.
    private static readonly int[] DaySearchOrder = [0, -1, 1];

    private readonly ISiteCalendarDirectory _directory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the calendar over a directory of plants.</summary>
    /// <param name="directory">Where a plant's zone and shift table come from.</param>
    /// <param name="timeProvider">
    /// The clock, for the two "right now" methods only. Injected rather than called statically (K1),
    /// so a test can stand at 05:59 on a change-over day without waiting for one.
    /// </param>
    public ProductionCalendar(ISiteCalendarDirectory directory, TimeProvider timeProvider)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public ProductionDay GetProductionDay(DateTimeOffset instant, string siteId) =>
        Locate(instant, CalendarFor(siteId)).Day;

    /// <inheritdoc />
    public Shift GetShift(DateTimeOffset instant, string siteId) =>
        Locate(instant, CalendarFor(siteId)).Shift;

    /// <inheritdoc />
    public ShiftBoundaries GetShiftBoundaries(ProductionDay day, Shift shift, string siteId) =>
        Boundaries(day, shift, CalendarFor(siteId));

    /// <inheritdoc />
    public ProductionDay CurrentProductionDay(string siteId) =>
        GetProductionDay(_timeProvider.GetUtcNow(), siteId);

    /// <inheritdoc />
    public Shift CurrentShift(string siteId) => GetShift(_timeProvider.GetUtcNow(), siteId);

    private static ShiftBoundaries Boundaries(ProductionDay day, Shift shift, SiteCalendar calendar)
    {
        var schedule = calendar.Schedule;
        var definition = schedule.Definition(shift);

        // Built by walking forward from the moment the production day opens, not from the shift's own
        // clock reading. For shift C that walk crosses midnight on its own — 06:00 plus sixteen hours
        // is 22:00 the next calendar date — which is precisely the arithmetic the production day
        // definition describes, done once instead of special-cased per shift.
        var opensAt = day.Date.ToDateTime(schedule.DayStart);
        var startsAt = opensAt.Add(schedule.OffsetIntoDay(shift));
        var endsAt = startsAt.Add(definition.NominalLength);

        return new ShiftBoundaries(
            day,
            shift,
            Resolve(startsAt, calendar.TimeZone),
            Resolve(endsAt, calendar.TimeZone));
    }

    /// <summary>Finds the one shift whose real boundaries contain an instant.</summary>
    /// <remarks>
    /// <para>
    /// <b>The answer is read off the boundaries rather than off the clock, and that is the fix for a
    /// real contradiction.</b> Reading the wall clock and looking the reading up in the shift table is
    /// correct only while no shift boundary falls inside the hour a clock change repeats or skips. The
    /// NovaVolt table — 06:00, 14:00, 22:00 — never does, so the shortcut looked right; a plant whose
    /// shift turns over at 02:30 breaks it, and docs/plans §M10 brings exactly that kind of table.
    /// </para>
    /// <para>
    /// What broke: in the repeated hour, both readings of 02:00 look up to the same shift, but
    /// <see cref="Resolve"/> puts that shift's 02:30 boundary at the <b>first</b> 02:30 there is. So
    /// the second 02:00 was named by <c>GetShift</c> as a shift whose own interval had already ended —
    /// <c>GetShift(t)</c> and <c>GetShiftBoundaries(...).Contains(t)</c> disagreeing about one instant,
    /// which is precisely the invariant this class documents and every shift-scoped count depends on.
    /// </para>
    /// <para>
    /// Deriving both answers from the same intervals makes the invariant hold by construction instead
    /// of by coincidence: there is now one rule for turning a clock reading into an instant, and both
    /// directions go through it. A day either side of the wall-clock guess is searched because a
    /// production day is not a calendar day and a clock change moves the boundary — never further than
    /// that, since no zone shifts its clock by a day.
    /// </para>
    /// </remarks>
    private static ShiftBoundaries Locate(DateTimeOffset instant, SiteCalendar calendar)
    {
        var guess = WallClockDay(instant, calendar);

        foreach (var dayOffset in DaySearchOrder)
        {
            var day = ProductionDay.On(guess.Date.AddDays(dayOffset));

            foreach (var definition in calendar.Schedule.Definitions)
            {
                var boundaries = Boundaries(day, definition.Shift, calendar);

                // Half-open, so a shift the spring clock change squeezed to nothing contains no
                // instant at all and is stepped over rather than being allowed to claim its start.
                if (boundaries.Contains(instant))
                {
                    return boundaries;
                }
            }
        }

        // Unreachable while the shift table covers the clock exactly once: the intervals of
        // consecutive production days meet end to start by construction, so they tile the timeline.
        // Throwing means a future change that breaks the tiling is found here rather than filing a
        // measurement under a shift it did not happen in.
        throw new InvalidOperationException(
            $"No shift of {calendar.SiteId} contains {instant:O}, which a validated table cannot happen to.");
    }

    /// <summary>The production day a plant's clock reading names, before boundaries refine it.</summary>
    /// <remarks>
    /// Before the opening shift, the plant is still finishing the previous cycle. This is the
    /// definition of a production day stated in local time — the reading on the wall, on the date the
    /// wall says — and it is the starting guess <see cref="Locate"/> searches around, correct on its
    /// own everywhere the two clock changes of the year do not move a boundary.
    /// </remarks>
    private static ProductionDay WallClockDay(DateTimeOffset instant, SiteCalendar calendar)
    {
        var wallClock = WallClockAt(instant, calendar);
        var date = DateOnly.FromDateTime(wallClock);

        return TimeOnly.FromDateTime(wallClock) >= calendar.Schedule.DayStart
            ? ProductionDay.On(date)
            : ProductionDay.On(date.AddDays(-1));
    }

    /// <summary>Turns a reading of a plant's clock into the instant it names.</summary>
    /// <remarks>
    /// <para>
    /// One rule, three cases: <b>the earliest instant whose local clock reads at or after
    /// <paramref name="wallClock"/></b>.
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// On an ordinary day that is just the instant with that reading.
    /// </item>
    /// <item>
    /// In the repeated hour of an autumn change-over the reading happens twice, and the rule takes the
    /// <b>first</b>. So shift C starts at the first 22:00 there is, and the extra hour falls inside the
    /// shift, making it nine hours long — which is what actually happened on the floor.
    /// </item>
    /// <item>
    /// In the skipped hour of a spring change-over the reading never happens, and the rule takes the
    /// moment the clock jumped past it.
    /// </item>
    /// </list>
    /// <para>
    /// Choosing one rule for all three is what keeps <see cref="GetShift"/> and
    /// <see cref="GetShiftBoundaries"/> from contradicting each other: an instant is inside a shift's
    /// boundaries exactly when <c>GetShift</c> names that shift, and the end of one shift is exactly
    /// the start of the next with no gap and no overlap. Letting the framework's default decide the
    /// ambiguous case would break that on one Sunday a year, in a way no ordinary test would see.
    /// </para>
    /// </remarks>
    private static DateTimeOffset Resolve(DateTime wallClock, TimeZoneInfo zone)
    {
        if (zone.IsAmbiguousTime(wallClock))
        {
            // An instant is the reading minus the offset, so the largest offset is the earliest
            // instant — the first of the two times the clock read this.
            return new DateTimeOffset(wallClock, zone.GetAmbiguousTimeOffsets(wallClock).Max());
        }

        if (zone.IsInvalidTime(wallClock))
        {
            return FirstInstantAtOrAfter(wallClock, zone);
        }

        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }

    /// <summary>Finds the moment the clock skipped past a reading that never happened.</summary>
    /// <remarks>
    /// Bisection rather than reading <see cref="TimeZoneInfo.GetAdjustmentRules"/>, whose shape
    /// differs between the Windows registry and the IANA database and would need two code paths to
    /// get one answer. Local time is strictly increasing across this window: the nearest ambiguous
    /// hour is months away, because no zone moves its clock twice inside two days.
    /// </remarks>
    private static DateTimeOffset FirstInstantAtOrAfter(DateTime wallClock, TimeZoneInfo zone)
    {
        var low = DateTime.SpecifyKind(wallClock, DateTimeKind.Utc).AddHours(-30);
        var high = DateTime.SpecifyKind(wallClock, DateTimeKind.Utc).AddHours(30);

        while (high - low > TimeSpan.FromTicks(1))
        {
            var middle = low.AddTicks((high - low).Ticks / 2);

            if (TimeZoneInfo.ConvertTimeFromUtc(middle, zone) < wallClock)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        var instant = new DateTimeOffset(high, TimeSpan.Zero);

        return instant.ToOffset(zone.GetUtcOffset(instant));
    }

    private static DateTime WallClockAt(DateTimeOffset instant, SiteCalendar calendar) =>
        TimeZoneInfo.ConvertTime(instant, calendar.TimeZone).DateTime;

    private SiteCalendar CalendarFor(string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        return _directory.Find(siteId) ?? throw new UnknownSiteException(siteId);
    }
}
