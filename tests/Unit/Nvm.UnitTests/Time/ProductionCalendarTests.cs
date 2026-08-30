using Microsoft.Extensions.Time.Testing;
using Nvm.Time;

namespace Nvm.UnitTests.Time;

public sealed class ProductionCalendarTests
{
    [Theory]
    [InlineData(SiteCalendars.HaiPhong)]
    [InlineData(SiteCalendars.Leipzig)]
    public void D4_FiveFiftyNineAndSixOhOne_AreOneProductionDayApart(string siteId)
    {
        // ★ D4. Stated for BOTH plants because one plant proves nothing about the other: NV1 is a
        // fixed UTC+7 and DE1 moves its clock, and a calculation that got the boundary from an offset
        // rather than from the wall clock would pass at NV1 every day of the year.
        var calendar = SiteCalendars.Build();

        var before = calendar.GetProductionDay(
            SiteCalendars.LocalAt(siteId, 2026, 8, 26, 5, 59), siteId);
        var after = calendar.GetProductionDay(
            SiteCalendars.LocalAt(siteId, 2026, 8, 26, 6, 1), siteId);

        before.ShouldBe(ProductionDay.On(2026, 8, 25));
        after.ShouldBe(ProductionDay.On(2026, 8, 26));
        before.DaysUntil(after).ShouldBe(1);
    }

    [Theory]
    [InlineData(SiteCalendars.HaiPhong)]
    [InlineData(SiteCalendars.Leipzig)]
    public void NightShift_BelongsToTheDayItStartedOn(string siteId)
    {
        // 23:47 on the 25th and 02:13 on the 26th are the same shift and the same production day,
        // which is the sentence the whole assembly exists to make true (docs/scope.md §2.3).
        var calendar = SiteCalendars.Build();
        var beforeMidnight = SiteCalendars.LocalAt(siteId, 2026, 8, 25, 23, 47);
        var afterMidnight = SiteCalendars.LocalAt(siteId, 2026, 8, 26, 2, 13);

        calendar.GetProductionDay(beforeMidnight, siteId).ShouldBe(ProductionDay.On(2026, 8, 25));
        calendar.GetProductionDay(afterMidnight, siteId).ShouldBe(ProductionDay.On(2026, 8, 25));
        calendar.GetShift(beforeMidnight, siteId).ShouldBe(Shift.C);
        calendar.GetShift(afterMidnight, siteId).ShouldBe(Shift.C);
    }

    [Theory]
    [InlineData(6, 0, Shift.A)]
    [InlineData(13, 59, Shift.A)]
    [InlineData(14, 0, Shift.B)]
    [InlineData(21, 59, Shift.B)]
    [InlineData(22, 0, Shift.C)]
    [InlineData(5, 59, Shift.C)]
    public void GetShift_ReadsTheBoundariesOffTheLocalClock(int hour, int minute, Shift expected)
    {
        var calendar = SiteCalendars.Build();
        var instant = SiteCalendars.LocalAt(SiteCalendars.HaiPhong, 2026, 8, 25, hour, minute);

        calendar.GetShift(instant, SiteCalendars.HaiPhong).ShouldBe(expected);
    }

    [Fact]
    public void GetShiftBoundaries_ReturnsAbsoluteInstants_NotClockReadings()
    {
        // NV1 is UTC+7 with no daylight saving, so shift C of the 25th runs from 22:00 local — 15:00
        // UTC — to 06:00 local the next day. The point of the assertion is the offset: a caller
        // selecting rows uses these, and rows carry instants.
        var calendar = SiteCalendars.Build();

        var boundaries = calendar.GetShiftBoundaries(
            ProductionDay.On(2026, 8, 25), Shift.C, SiteCalendars.HaiPhong);

        boundaries.Start.ShouldBe(new DateTimeOffset(2026, 8, 25, 22, 0, 0, TimeSpan.FromHours(7)));
        boundaries.End.ShouldBe(new DateTimeOffset(2026, 8, 26, 6, 0, 0, TimeSpan.FromHours(7)));
        boundaries.Duration.ShouldBe(TimeSpan.FromHours(8));
        boundaries.Start.UtcDateTime.ShouldBe(new DateTime(2026, 8, 25, 15, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(SiteCalendars.HaiPhong)]
    [InlineData(SiteCalendars.Leipzig)]
    public void GetShiftBoundaries_LeavesNoGapAndNoOverlapBetweenConsecutiveShifts(string siteId)
    {
        // Half-open intervals that meet exactly. A millisecond owned by two shifts is a measurement
        // counted twice; a millisecond owned by none is one that vanishes from every shift report.
        var calendar = SiteCalendars.Build();
        var day = ProductionDay.On(2026, 8, 25);

        var a = calendar.GetShiftBoundaries(day, Shift.A, siteId);
        var b = calendar.GetShiftBoundaries(day, Shift.B, siteId);
        var c = calendar.GetShiftBoundaries(day, Shift.C, siteId);
        var nextA = calendar.GetShiftBoundaries(day.Next(), Shift.A, siteId);

        a.End.ShouldBe(b.Start);
        b.End.ShouldBe(c.Start);
        c.End.ShouldBe(nextA.Start);

        a.Contains(a.Start).ShouldBeTrue();
        a.Contains(a.End).ShouldBeFalse();
        b.Contains(a.End).ShouldBeTrue();
    }

    [Theory]
    [InlineData(SiteCalendars.HaiPhong)]
    [InlineData(SiteCalendars.Leipzig)]
    public void GetShiftBoundaries_AgreesWithGetShiftAtEveryEdge(string siteId)
    {
        // The invariant that ties the two halves of the interface together: an instant is inside a
        // shift's boundaries exactly when GetShift names that shift. Checked at the edges, where a
        // resolution rule chosen differently in the two methods would show up.
        var calendar = SiteCalendars.Build();
        var day = ProductionDay.On(2026, 8, 25);

        foreach (var shift in new[] { Shift.A, Shift.B, Shift.C })
        {
            var boundaries = calendar.GetShiftBoundaries(day, shift, siteId);

            calendar.GetShift(boundaries.Start, siteId).ShouldBe(shift);
            calendar.GetProductionDay(boundaries.Start, siteId).ShouldBe(day);

            var lastInstant = boundaries.End.AddTicks(-1);
            calendar.GetShift(lastInstant, siteId).ShouldBe(shift);
            calendar.GetProductionDay(lastInstant, siteId).ShouldBe(day);

            calendar.GetShift(boundaries.End, siteId).ShouldNotBe(shift);
        }
    }

    [Fact]
    public void CurrentProductionDay_ReadsTheInjectedClock()
    {
        // K1: the clock is a dependency. A calendar calling DateTime.UtcNow could not be asked what
        // production day it is at 05:59 without a test that waits until 05:59.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 22, 59, 0, TimeSpan.Zero));
        var calendar = SiteCalendars.Build(clock);

        // 22:59 UTC is 05:59 the next morning in Hai Phong: still the night shift of the 25th.
        calendar.CurrentProductionDay(SiteCalendars.HaiPhong).ShouldBe(ProductionDay.On(2026, 8, 25));
        calendar.CurrentShift(SiteCalendars.HaiPhong).ShouldBe(Shift.C);

        clock.Advance(TimeSpan.FromMinutes(2));

        calendar.CurrentProductionDay(SiteCalendars.HaiPhong).ShouldBe(ProductionDay.On(2026, 8, 26));
        calendar.CurrentShift(SiteCalendars.HaiPhong).ShouldBe(Shift.A);
    }

    [Fact]
    public void AnUnknownSite_ThrowsAndNamesIt()
    {
        // Same rule M2/C02 applied to an unrecognised Sparkplug alias: do not guess. Defaulting to UTC
        // would give a new plant plausible-looking numbers filed against the wrong day for a month.
        var calendar = SiteCalendars.Build();

        var thrown = Should.Throw<UnknownSiteException>(
            () => calendar.GetProductionDay(DateTimeOffset.UnixEpoch, "US1"));

        thrown.SiteId.ShouldBe("US1");
        thrown.Message.ShouldContain("US1");
    }

    [Theory]
    [InlineData("Asia/Ho_Chi_Minh")]
    [InlineData("Europe/Berlin")]
    public void BothIanaIdsResolve_WhichIsWhatAdr020Bought(string ianaId)
    {
        // R-M3-4. These ids only resolve because InvariantGlobalization is off (ADR-020). Someone
        // turning it back on to shrink a container would otherwise break the production calendar, and
        // the first symptom would be a shift report six months later rather than a red test.
        var zone = Should.NotThrow(() => SiteTimeZone.Of(ianaId));

        zone.ShouldNotBeNull();
    }

    [Fact]
    public void AnUnknownZoneId_SaysWhereToLook()
    {
        var thrown = Should.Throw<TimeZoneNotFoundException>(() => SiteTimeZone.Of("Mars/Olympus_Mons"));

        thrown.Message.ShouldContain("InvariantGlobalization");
    }
}
