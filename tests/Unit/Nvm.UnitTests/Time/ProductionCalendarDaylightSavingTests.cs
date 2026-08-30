using Nvm.Time;

namespace Nvm.UnitTests.Time;

/// <summary>★ D3 — the two days a year that every MES gets wrong at least once.</summary>
/// <remarks>
/// <para>
/// Leipzig moves its clocks on the last Sunday of March and the last Sunday of October: 29 March 2026
/// and 25 October 2026. On the first, 02:00 becomes 03:00 and the hour in between never happens; on
/// the second, 03:00 becomes 02:00 and the hour in between happens twice.
/// </para>
/// <para>
/// Shift C spans both changes, because it is the one that runs through the small hours. So it lasts
/// seven hours once a year and nine hours once a year — with nobody on the floor doing anything
/// differently, and with no row anywhere in the system looking wrong. This is why DE1 is in the model
/// at all (docs/scope.md §2.3): it is a live test case, not decoration.
/// </para>
/// </remarks>
public sealed class ProductionCalendarDaylightSavingTests
{
    /// <summary>The Sunday the clocks go forward: 02:00 local becomes 03:00, and an hour vanishes.</summary>
    private static readonly ProductionDay SpringForwardNight = ProductionDay.On(2026, 3, 28);

    /// <summary>The Sunday the clocks go back: 03:00 local becomes 02:00, and an hour repeats.</summary>
    private static readonly ProductionDay AutumnBackNight = ProductionDay.On(2026, 10, 24);

    [Fact]
    public void D3_AtLeipzig_ShiftCIsSevenHoursOnTheSpringChangeOver()
    {
        // Nominal 22:00 to 06:00, which the shift board still says. Actual seven hours, because the
        // clock skipped one. An OEE calculation dividing output by "8 hours × capacity" reports this
        // shift as 12,5 % less efficient, and there is a meeting about it.
        var calendar = SiteCalendars.Build();

        var boundaries = calendar.GetShiftBoundaries(SpringForwardNight, Shift.C, SiteCalendars.Leipzig);

        boundaries.Duration.ShouldBe(TimeSpan.FromHours(7));
        boundaries.Start.ShouldBe(new DateTimeOffset(2026, 3, 28, 22, 0, 0, TimeSpan.FromHours(1)));
        boundaries.End.ShouldBe(new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void D3_AtLeipzig_ShiftCIsNineHoursOnTheAutumnChangeOver()
    {
        var calendar = SiteCalendars.Build();

        var boundaries = calendar.GetShiftBoundaries(AutumnBackNight, Shift.C, SiteCalendars.Leipzig);

        boundaries.Duration.ShouldBe(TimeSpan.FromHours(9));
        boundaries.Start.ShouldBe(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.FromHours(2)));
        boundaries.End.ShouldBe(new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void D3_AtHaiPhong_TheSameTwoNightsAreEightHoursLikeEveryOtherNight()
    {
        // The control. NV1 is a fixed UTC+7, so a calculation that ignored the zone entirely would
        // still pass here — which is the whole reason DE1 is in the assertions above.
        var calendar = SiteCalendars.Build();

        calendar.GetShiftBoundaries(SpringForwardNight, Shift.C, SiteCalendars.HaiPhong)
            .Duration.ShouldBe(TimeSpan.FromHours(8));
        calendar.GetShiftBoundaries(AutumnBackNight, Shift.C, SiteCalendars.HaiPhong)
            .Duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Fact]
    public void D3_TheRepeatedHour_FallsInExactlyOneShiftAndOneProductionDay()
    {
        // 02:30 on 25 October happens twice at Leipzig: once at UTC+2 and once, an hour later, at
        // UTC+1. Two different instants, one clock reading. Both belong to shift C of production day
        // 24 October — the shift simply contains the hour twice, which is what "nine hours" means.
        var calendar = SiteCalendars.Build();
        var first = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2));
        var second = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(1));

        (second - first).ShouldBe(TimeSpan.FromHours(1));

        var boundaries = calendar.GetShiftBoundaries(AutumnBackNight, Shift.C, SiteCalendars.Leipzig);

        foreach (var instant in new[] { first, second })
        {
            calendar.GetShift(instant, SiteCalendars.Leipzig).ShouldBe(Shift.C);
            calendar.GetProductionDay(instant, SiteCalendars.Leipzig).ShouldBe(AutumnBackNight);
            boundaries.Contains(instant).ShouldBeTrue();
        }

        // And the shift's own first hour, which a calendar using the standard offset all year would
        // place an hour late and therefore exclude. Without this line the assertions above all pass
        // against the wrong implementation — measured, see benchmarks.md M3.
        boundaries.Contains(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.FromHours(2)))
            .ShouldBeTrue();
    }

    [Fact]
    public void D3_TheSkippedHour_LeavesNoInstantUnclaimed()
    {
        // Around the spring gap there is no clock reading between 01:59:59 and 03:00:00, so the test
        // has to be stated on instants rather than on readings: the last instant before the jump and
        // the first after it both belong to shift C of the 28th, with nothing in between belonging to
        // anything else.
        var calendar = SiteCalendars.Build();
        var justBefore = new DateTimeOffset(2026, 3, 29, 1, 59, 59, TimeSpan.FromHours(1));
        var justAfter = new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2));

        (justAfter - justBefore).ShouldBe(TimeSpan.FromSeconds(1));

        var boundaries = calendar.GetShiftBoundaries(SpringForwardNight, Shift.C, SiteCalendars.Leipzig);

        foreach (var instant in new[] { justBefore, justAfter })
        {
            calendar.GetShift(instant, SiteCalendars.Leipzig).ShouldBe(Shift.C);
            calendar.GetProductionDay(instant, SiteCalendars.Leipzig).ShouldBe(SpringForwardNight);
            boundaries.Contains(instant).ShouldBeTrue();
        }

        // 06:30 is already shift A of the next production day, and shift C must not still claim it. A
        // calendar using the standard offset all year ends this shift an hour late and swallows it.
        var morningAfter = new DateTimeOffset(2026, 3, 29, 6, 30, 0, TimeSpan.FromHours(2));

        boundaries.Contains(morningAfter).ShouldBeFalse();
        calendar.GetShift(morningAfter, SiteCalendars.Leipzig).ShouldBe(Shift.A);
        calendar.GetProductionDay(morningAfter, SiteCalendars.Leipzig)
            .ShouldBe(SpringForwardNight.Next());
    }

    [Theory]
    [InlineData(2026, 3, 28, 23)]
    [InlineData(2026, 10, 24, 25)]
    public void D3_TheThreeShiftsStillTileTheChangeOverDayExactly(int year, int month, int day, int hours)
    {
        // A day that is 23 or 25 hours long is still a production day made of three shifts that meet
        // exactly. If the resolution rule had been chosen differently for the two ends of shift C,
        // this is where a missing or double-counted hour would appear.
        //
        // The total is asserted as well as the tiling, and that is the half that discriminates: three
        // fixed eight-hour shifts tile perfectly too, and add up to a day that never existed.
        var calendar = SiteCalendars.Build();
        var production = ProductionDay.On(year, month, day);

        var a = calendar.GetShiftBoundaries(production, Shift.A, SiteCalendars.Leipzig);
        var b = calendar.GetShiftBoundaries(production, Shift.B, SiteCalendars.Leipzig);
        var c = calendar.GetShiftBoundaries(production, Shift.C, SiteCalendars.Leipzig);
        var nextDay = calendar.GetShiftBoundaries(production.Next(), Shift.A, SiteCalendars.Leipzig);

        a.End.ShouldBe(b.Start);
        b.End.ShouldBe(c.Start);
        c.End.ShouldBe(nextDay.Start);
        (a.Duration + b.Duration + c.Duration).ShouldBe(nextDay.Start - a.Start);
        (nextDay.Start - a.Start).ShouldBe(TimeSpan.FromHours(hours));
    }

    [Fact]
    public void D3_AShiftBoundaryInsideTheSkippedHour_ResolvesToTheMomentTheClockJumped()
    {
        // NovaVolt's own boundaries — 06:00, 14:00, 22:00 — never land inside a gap, so this uses a
        // table that does. The rule has to be stated and tested rather than left to whatever the
        // framework does by default, because M10 brings sites with other tables and the first one
        // whose shift starts at 02:30 must not silently get an arbitrary answer.
        var schedule = ShiftSchedule.Create(
        [
            new ShiftDefinition(Shift.A, new TimeOnly(2, 30), TimeSpan.FromHours(12)),
            new ShiftDefinition(Shift.B, new TimeOnly(14, 30), TimeSpan.FromHours(12)),
        ]);
        var calendar = new ProductionCalendar(
            new InMemorySiteCalendarDirectory(
                new SiteCalendar(SiteCalendars.Leipzig, SiteTimeZone.Of("Europe/Berlin"), schedule)),
            TimeProvider.System);

        var boundaries = calendar.GetShiftBoundaries(
            ProductionDay.On(2026, 3, 29), Shift.A, SiteCalendars.Leipzig);

        // 02:30 never happened on 29 March; the clock went straight from 01:59:59 to 03:00:00.
        boundaries.Start.ShouldBe(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)));
        boundaries.Duration.ShouldBe(TimeSpan.FromHours(11.5));

        // And the two halves of the interface still agree, which is the property that would break if
        // the gap rule here and the local-clock comparison in GetShift disagreed by an hour.
        calendar.GetShift(boundaries.Start, SiteCalendars.Leipzig).ShouldBe(Shift.A);
        calendar.GetShift(boundaries.Start.AddTicks(-1), SiteCalendars.Leipzig).ShouldBe(Shift.B);
    }

    [Fact]
    public void D3_AShiftBoundaryInsideTheRepeatedHour_NamesTheShiftThatActuallyContainsTheInstant()
    {
        // The autumn twin of the test above, and the one that caught a real contradiction. When the
        // boundary is 02:30, the second reading of 02:00 is AFTER the shift that ended at the first
        // 02:30 — so naming the shift by looking the wall clock up in the table returns a shift whose
        // own interval had already closed. GetShift said B; B.Contains said false; both about the same
        // instant. NovaVolt's 06/14/22 table hides this because none of its boundaries land in the
        // repeated hour, which is exactly why the general table has to be the one under test.
        var calendar = OverlappingChangeOverSite();
        var second02 = new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(1));
        var first02 = new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(2));

        (second02 - first02).ShouldBe(TimeSpan.FromHours(1));

        // The first 02:00 is still the closing shift of the 24th; the repeated hour has moved the
        // second one into the opening shift of the 25th, an hour whose clock reading says otherwise.
        calendar.GetShift(first02, SiteCalendars.Leipzig).ShouldBe(Shift.B);
        calendar.GetProductionDay(first02, SiteCalendars.Leipzig).ShouldBe(ProductionDay.On(2026, 10, 24));
        calendar.GetShift(second02, SiteCalendars.Leipzig).ShouldBe(Shift.A);
        calendar.GetProductionDay(second02, SiteCalendars.Leipzig).ShouldBe(ProductionDay.On(2026, 10, 25));

        // The opening shift of the 25th is thirteen hours because the extra hour falls inside it,
        // and the whole point is that its own boundaries say so.
        var opening = calendar.GetShiftBoundaries(
            ProductionDay.On(2026, 10, 25), Shift.A, SiteCalendars.Leipzig);

        opening.Duration.ShouldBe(TimeSpan.FromHours(13));
        opening.Contains(second02).ShouldBeTrue();
        opening.Contains(first02).ShouldBeFalse();
    }

    [Theory]
    [InlineData(2026, 3, 29)]
    [InlineData(2026, 10, 25)]
    public void D3_OnEitherChangeOver_GetShiftAgreesWithTheBoundariesForEveryMinute(
        int year,
        int month,
        int day)
    {
        // The invariant the class documents, stated as a sweep instead of as prose: for every instant,
        // the shift GetShift names is the shift whose own interval contains it, and it belongs to the
        // production day those boundaries are filed under. Swept minute by minute across both change
        // -overs on a table whose boundary sits inside the moved hour, because that is the only place
        // the two halves can disagree — and one instant filed under the wrong shift is a cell counted
        // in the wrong shift's yield.
        var calendar = OverlappingChangeOverSite();
        var cursor = new DateTimeOffset(new DateTime(year, month, day, 0, 0, 0), TimeSpan.Zero)
            .AddHours(-4);
        var stop = cursor.AddHours(32);

        while (cursor < stop)
        {
            var shift = calendar.GetShift(cursor, SiteCalendars.Leipzig);
            var productionDay = calendar.GetProductionDay(cursor, SiteCalendars.Leipzig);
            var boundaries = calendar.GetShiftBoundaries(productionDay, shift, SiteCalendars.Leipzig);

            boundaries.Contains(cursor).ShouldBeTrue(
                $"{cursor:O} was named {productionDay}/{shift}, whose interval is "
                + $"[{boundaries.Start:O}, {boundaries.End:O}).");

            cursor = cursor.AddMinutes(1);
        }
    }

    /// <summary>A plant whose shift turns over at 02:30 — inside both hours the clock moves.</summary>
    /// <remarks>
    /// Not a NovaVolt table. It is the smallest shift table that puts a boundary in the skipped hour
    /// and in the repeated one, which is the case docs/plans M10 brings and the case the 06/14/22
    /// table cannot exercise at all.
    /// </remarks>
    private static ProductionCalendar OverlappingChangeOverSite()
    {
        var schedule = ShiftSchedule.Create(
        [
            new ShiftDefinition(Shift.A, new TimeOnly(2, 30), TimeSpan.FromHours(12)),
            new ShiftDefinition(Shift.B, new TimeOnly(14, 30), TimeSpan.FromHours(12)),
        ]);

        return new ProductionCalendar(
            new InMemorySiteCalendarDirectory(
                new SiteCalendar(SiteCalendars.Leipzig, SiteTimeZone.Of("Europe/Berlin"), schedule)),
            TimeProvider.System);
    }
}
