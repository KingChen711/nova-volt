using Microsoft.Extensions.Time.Testing;
using Nvm.Time;

namespace Nvm.UnitTests.Time;

/// <summary>Hai nhà máy trong seed model, dựng theo cách calendar test cần.</summary>
/// <remarks>
/// Zone id là các id trong <c>deploy/seed/factory-model.r3.json</c>, giữ nguyên cách viết. C04 thay
/// stand-in này bằng adapter đọc chính factory model; đến khi đó, test này nói về calendar, không nói
/// về nơi lưu zone của nhà máy.
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

    /// <summary>Một instant, viết theo reading từ clock của một nhà máy.</summary>
    /// <remarks>
    /// Đi qua boundary resolution của chính calendar để test có thể nói "23:47 local ngày 25" mà không
    /// cần biết offset đêm đó là bao nhiêu.
    /// </remarks>
    internal static DateTimeOffset LocalAt(string siteId, int year, int month, int day, int hour, int minute)
    {
        var zone = SiteTimeZone.Of(siteId == Leipzig ? "Europe/Berlin" : "Asia/Ho_Chi_Minh");
        var wallClock = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }
}
