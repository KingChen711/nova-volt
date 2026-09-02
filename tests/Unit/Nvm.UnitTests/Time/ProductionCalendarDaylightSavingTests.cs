using Nvm.Time;

namespace Nvm.UnitTests.Time;

/// <summary>★ D3 — hai ngày trong năm mà mọi MES đều làm sai ít nhất một lần.</summary>
/// <remarks>
/// <para>
/// Leipzig đổi giờ vào Chủ nhật cuối cùng của tháng Ba và Chủ nhật cuối cùng của tháng Mười: 29 tháng
/// 3 năm 2026 và 25 tháng 10 năm 2026. Lần đầu, 02:00 trở thành 03:00 và giờ ở giữa không bao giờ xảy
/// ra; lần sau, 03:00 trở thành 02:00 và giờ ở giữa xảy ra hai lần.
/// </para>
/// <para>
/// Shift C trải dài qua cả hai lần đổi giờ, vì đây là shift chạy xuyên qua những giờ khuya. Vậy nên
/// nó kéo dài bảy giờ một lần mỗi năm và chín giờ một lần mỗi năm — trong khi không ai trên sàn làm
/// gì khác đi, và không có dòng nào trong hệ thống trông có vẻ sai cả. Đây chính là lý do DE1 có mặt
/// trong model (docs/scope.md §2.3): nó là một test case sống, không phải để trang trí.
/// </para>
/// </remarks>
public sealed class ProductionCalendarDaylightSavingTests
{
    /// <summary>Chủ nhật đồng hồ chỉnh tới: 02:00 giờ địa phương thành 03:00, và một giờ biến mất.</summary>
    private static readonly ProductionDay SpringForwardNight = ProductionDay.On(2026, 3, 28);

    /// <summary>Chủ nhật đồng hồ chỉnh lùi: 03:00 giờ địa phương thành 02:00, và một giờ lặp lại.</summary>
    private static readonly ProductionDay AutumnBackNight = ProductionDay.On(2026, 10, 24);

    [Fact]
    public void D3_AtLeipzig_ShiftCIsSevenHoursOnTheSpringChangeOver()
    {
        // Danh nghĩa là 22:00 tới 06:00, đúng như bảng shift vẫn ghi. Thực tế là bảy giờ, vì đồng hồ
        // đã nhảy mất một giờ. Một phép tính OEE chia sản lượng cho "8 giờ × công suất" sẽ báo shift
        // này kém hiệu quả hơn 12,5%, và thế là có một cuộc họp về chuyện đó.
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
        // Đối chứng. NV1 là UTC+7 cố định, nên một phép tính bỏ qua hoàn toàn zone vẫn sẽ pass ở đây
        // — và đó chính là toàn bộ lý do DE1 có mặt trong các assertion ở trên.
        var calendar = SiteCalendars.Build();

        calendar.GetShiftBoundaries(SpringForwardNight, Shift.C, SiteCalendars.HaiPhong)
            .Duration.ShouldBe(TimeSpan.FromHours(8));
        calendar.GetShiftBoundaries(AutumnBackNight, Shift.C, SiteCalendars.HaiPhong)
            .Duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Fact]
    public void D3_TheRepeatedHour_FallsInExactlyOneShiftAndOneProductionDay()
    {
        // 02:30 ngày 25 tháng 10 xảy ra hai lần ở Leipzig: một lần ở UTC+2 và một lần, một giờ sau
        // đó, ở UTC+1. Hai thời điểm khác nhau, một cách đọc đồng hồ. Cả hai đều thuộc shift C của
        // production day 24 tháng 10 — shift đơn giản là chứa giờ đó hai lần, và đó chính là ý nghĩa
        // của "chín giờ".
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

        // Và giờ đầu tiên của chính shift đó, thứ mà một calendar dùng offset chuẩn quanh năm sẽ đặt
        // muộn mất một giờ và vì thế loại nó ra. Thiếu dòng này thì mọi assertion ở trên vẫn pass với
        // một implementation sai — đã đo được, xem benchmarks.md M3.
        boundaries.Contains(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.FromHours(2)))
            .ShouldBeTrue();
    }

    [Fact]
    public void D3_TheSkippedHour_LeavesNoInstantUnclaimed()
    {
        // Quanh khoảng trống mùa xuân không có cách đọc đồng hồ nào giữa 01:59:59 và 03:00:00, nên
        // test phải được phát biểu trên các thời điểm thay vì trên các cách đọc: thời điểm cuối cùng
        // trước cú nhảy và thời điểm đầu tiên sau đó đều thuộc shift C của ngày 28, và không có gì ở
        // giữa thuộc về bất cứ thứ gì khác.
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

        // 06:30 đã thuộc shift A của production day kế tiếp, và shift C không được phép vẫn còn nhận
        // nó về mình. Một calendar dùng offset chuẩn quanh năm sẽ kết thúc shift này muộn mất một giờ
        // và nuốt luôn thời điểm này.
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
        // Một ngày dài 23 hoặc 25 giờ vẫn là một production day gồm ba shift khớp nhau vừa khít. Nếu
        // quy tắc resolution được chọn khác đi cho hai đầu của shift C, đây sẽ là nơi một giờ bị
        // thiếu hoặc bị đếm hai lần sẽ lộ ra.
        //
        // Tổng số giờ được assert cùng với việc khớp mảnh (tiling), và đó chính là nửa mang tính phân
        // biệt: ba shift tám giờ cố định cũng khớp mảnh hoàn hảo, và cộng lại thành một ngày chưa
        // từng tồn tại.
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
        // Các boundary của riêng NovaVolt — 06:00, 14:00, 22:00 — không bao giờ rơi vào một khoảng
        // trống, nên ở đây dùng một bảng có rơi vào. Quy tắc phải được phát biểu và kiểm thử rõ ràng
        // thay vì để mặc cho framework làm gì đó theo mặc định, vì M10 mang tới các site với bảng
        // khác, và site đầu tiên có shift bắt đầu lúc 02:30 không được phép âm thầm nhận một câu trả
        // lời tùy tiện.
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

        // 02:30 chưa bao giờ xảy ra vào 29 tháng 3; đồng hồ nhảy thẳng từ 01:59:59 sang 03:00:00.
        boundaries.Start.ShouldBe(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)));
        boundaries.Duration.ShouldBe(TimeSpan.FromHours(11.5));

        // Và hai nửa của interface vẫn đồng thuận với nhau, đó chính là tính chất sẽ bị phá vỡ nếu
        // quy tắc về khoảng trống ở đây và phép so sánh local-clock trong GetShift lệch nhau một giờ.
        calendar.GetShift(boundaries.Start, SiteCalendars.Leipzig).ShouldBe(Shift.A);
        calendar.GetShift(boundaries.Start.AddTicks(-1), SiteCalendars.Leipzig).ShouldBe(Shift.B);
    }

    [Fact]
    public void D3_AShiftBoundaryInsideTheRepeatedHour_NamesTheShiftThatActuallyContainsTheInstant()
    {
        // Phiên bản mùa thu song sinh với test ở trên, và cũng là test đã bắt được một mâu thuẫn có
        // thật. Khi boundary là 02:30, cách đọc 02:00 lần thứ hai xảy ra SAU shift đã kết thúc vào
        // 02:30 lần đầu — nên việc đặt tên shift bằng cách tra đồng hồ tường trong bảng trả về một
        // shift mà khoảng thời gian của chính nó đã đóng lại rồi. GetShift nói B; B.Contains nói
        // false; cả hai đều nói về cùng một thời điểm. Bảng 06/14/22 của NovaVolt che giấu điều này
        // vì không boundary nào của nó rơi vào giờ bị lặp lại, và đó chính xác là lý do bảng tổng
        // quát mới là cái cần được kiểm thử.
        var calendar = OverlappingChangeOverSite();
        var second02 = new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(1));
        var first02 = new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(2));

        (second02 - first02).ShouldBe(TimeSpan.FromHours(1));

        // 02:00 lần đầu vẫn thuộc shift đóng của ngày 24; giờ bị lặp lại đã đưa lần thứ hai vào shift
        // mở của ngày 25, một giờ mà cách đọc đồng hồ lại nói điều ngược lại.
        calendar.GetShift(first02, SiteCalendars.Leipzig).ShouldBe(Shift.B);
        calendar.GetProductionDay(first02, SiteCalendars.Leipzig).ShouldBe(ProductionDay.On(2026, 10, 24));
        calendar.GetShift(second02, SiteCalendars.Leipzig).ShouldBe(Shift.A);
        calendar.GetProductionDay(second02, SiteCalendars.Leipzig).ShouldBe(ProductionDay.On(2026, 10, 25));

        // Shift mở của ngày 25 dài mười ba giờ vì giờ dư ra rơi vào bên trong nó, và toàn bộ trọng
        // tâm ở đây là chính các boundary của nó nói lên điều đó.
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
        // Bất biến mà class này ghi lại, được phát biểu dưới dạng một lượt quét thay vì bằng văn xuôi:
        // với mọi thời điểm, shift mà GetShift đặt tên chính là shift mà khoảng thời gian của nó chứa
        // thời điểm đó, và nó thuộc về production day mà các boundary đó được ghi nhận dưới. Quét
        // từng phút một qua cả hai lần đổi giờ trên một bảng có boundary nằm bên trong giờ bị dịch
        // chuyển, vì đó là nơi duy nhất hai nửa có thể bất đồng với nhau — và một thời điểm bị ghi
        // nhận sai shift là một cell bị tính vào yield của sai shift đó.
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

    /// <summary>Một nhà máy có shift chuyển ca lúc 02:30 — nằm trong cả hai giờ mà đồng hồ dịch chuyển.</summary>
    /// <remarks>
    /// Không phải bảng của NovaVolt. Đây là bảng shift nhỏ nhất đặt một boundary vào giờ bị bỏ qua và
    /// vào giờ bị lặp lại, đúng trường hợp mà docs/plans M10 mang tới và cũng là trường hợp mà bảng
    /// 06/14/22 hoàn toàn không thể thực hiện được.
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
