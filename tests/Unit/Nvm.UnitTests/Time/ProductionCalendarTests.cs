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
        // ★ D4. Được phát biểu cho CẢ HAI nhà máy vì một nhà máy không chứng minh được gì về nhà máy
        // kia: NV1 là UTC+7 cố định còn DE1 đổi giờ, và một phép tính lấy boundary từ offset thay vì
        // từ đồng hồ tường sẽ pass ở NV1 mọi ngày trong năm.
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
        // 23:47 ngày 25 và 02:13 ngày 26 là cùng một shift và cùng một production day, đó chính là
        // điều mà toàn bộ assembly này tồn tại để làm cho nó đúng (docs/scope.md §2.3).
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
        // NV1 là UTC+7 không có daylight saving, nên shift C của ngày 25 chạy từ 22:00 giờ địa phương
        // — 15:00 UTC — tới 06:00 giờ địa phương ngày hôm sau. Trọng tâm của assertion này là offset:
        // một caller chọn row dựa vào các giá trị này, và row mang theo các thời điểm cụ thể.
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
        // Các khoảng half-open khớp nhau chính xác. Một millisecond thuộc về hai shift là một phép đo
        // bị đếm hai lần; một millisecond không thuộc về shift nào là một phép đo biến mất khỏi mọi
        // báo cáo shift.
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
        // Bất biến gắn kết hai nửa của interface lại với nhau: một thời điểm nằm trong boundary của
        // một shift đúng khi GetShift đặt tên shift đó. Được kiểm tra ở các mép, nơi một quy tắc
        // resolution được chọn khác nhau giữa hai method sẽ lộ ra.
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
        // K1: đồng hồ là một dependency. Một calendar gọi DateTime.UtcNow sẽ không thể được hỏi bây
        // giờ là production day nào lúc 05:59 nếu không có một test phải chờ tới đúng 05:59.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 22, 59, 0, TimeSpan.Zero));
        var calendar = SiteCalendars.Build(clock);

        // 22:59 UTC là 05:59 sáng hôm sau ở Hải Phòng: vẫn là night shift của ngày 25.
        calendar.CurrentProductionDay(SiteCalendars.HaiPhong).ShouldBe(ProductionDay.On(2026, 8, 25));
        calendar.CurrentShift(SiteCalendars.HaiPhong).ShouldBe(Shift.C);

        clock.Advance(TimeSpan.FromMinutes(2));

        calendar.CurrentProductionDay(SiteCalendars.HaiPhong).ShouldBe(ProductionDay.On(2026, 8, 26));
        calendar.CurrentShift(SiteCalendars.HaiPhong).ShouldBe(Shift.A);
    }

    [Fact]
    public void AnUnknownSite_ThrowsAndNamesIt()
    {
        // Cùng quy tắc M2/C02 áp dụng cho một Sparkplug alias không nhận diện được: không được đoán.
        // Mặc định về UTC sẽ cho một nhà máy mới những con số trông có vẻ hợp lý nhưng bị ghi nhận
        // sai ngày suốt cả một tháng.
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
        // R-M3-4. Các id này chỉ resolve được vì InvariantGlobalization đang tắt (ADR-020). Nếu ai đó
        // bật lại nó để thu nhỏ container thì sẽ làm hỏng production calendar, và triệu chứng đầu
        // tiên sẽ là một báo cáo shift sai sáu tháng sau đó chứ không phải một test đỏ.
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
