using Microsoft.Extensions.Time.Testing;
using Nvm.Time;

namespace Nvm.UnitTests.Time;

/// <summary>The two plants of the seed model, built the way the calendar tests need them.</summary>
/// <remarks>
/// The zone ids are the ones in <c>deploy/seed/factory-model.r3.json</c>, spelled the same way. C04
/// replaces this stand-in with the adapter that reads the factory model itself; until then these
/// tests are about the calendar, not about where a plant's zone is stored.
/// </remarks>
internal static class SiteCalendars
{
    internal const string HaiPhong = "NV1";

    internal const string Leipzig = "DE1";

    internal static ProductionCalendar Build(TimeProvider? clock = null) =>
        new(
            new InMemorySiteCalendarDirectory(
                new SiteCalendar(HaiPhong, SiteTimeZone.Of("Asia/Ho_Chi_Minh"), ShiftSchedule.Default),
                new SiteCalendar(Leipzig, SiteTimeZone.Of("Europe/Berlin"), ShiftSchedule.Default)),
            clock ?? new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    /// <summary>An instant, written as a reading of one plant's clock.</summary>
    /// <remarks>
    /// Goes through the calendar's own boundary resolution so that a test can say "23:47 local on the
    /// 25th" without having to know what the offset was that night.
    /// </remarks>
    internal static DateTimeOffset LocalAt(string siteId, int year, int month, int day, int hour, int minute)
    {
        var zone = SiteTimeZone.Of(siteId == Leipzig ? "Europe/Berlin" : "Asia/Ho_Chi_Minh");
        var wallClock = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }
}
