namespace Nvm.Time;

/// <summary>Production calendar, tính toán theo local time của chính plant.</summary>
/// <remarks>
/// <para>
/// <b>Mọi thứ diễn ra theo local time, và đó chính là toàn bộ thiết kế.</b> Lối tắt hấp dẫn là trừ sáu
/// giờ trên instant UTC rồi lấy ngày — cách đó khớp với class này 363 ngày một năm ở DE1 và mọi ngày ở
/// NV1, và chính điều đó khiến nó nguy hiểm. docs/scope.md §2.3 gọi tên nó là cái bẫy. Một shift
/// boundary là một khẳng định về đồng hồ trên tường, và vào hai ngày đồng hồ đó bị chỉnh, một phép
/// toán offset chưa bao giờ nhìn vào zone sẽ đặt một phần của shift C vào sai production day — và ngày
/// bên cạnh sẽ sai đi cùng một lượng theo chiều ngược lại, nên tổng vẫn cộng khớp.
/// </para>
/// <para>
/// Chỗ duy nhất local time là chưa đủ là khi biến một shift boundary trở lại thành một instant, vì hai
/// lần đọc đồng hồ trong năm hoàn toàn không phải là instant: một giờ không bao giờ xảy ra và một giờ
/// xảy ra hai lần. <see cref="Resolve"/> phát biểu quy tắc cho cả hai trường hợp, và nó được phát biểu
/// một lần duy nhất để mặc định của thư viện không bao giờ được phép âm thầm quyết định thay.
/// </para>
/// </remarks>
public sealed class ProductionCalendar : IProductionCalendar
{
    // Đoán theo wall-clock trước, rồi một ngày mỗi bên cạnh nó. Sắp theo thứ tự này để trường hợp áp
    // đảo phổ biến trả lời ngay ở ngày ứng viên đầu tiên thay vì sau khi đã tìm ở ngày trước đó.
    private static readonly int[] DaySearchOrder = [0, -1, 1];

    private readonly ISiteCalendarDirectory _directory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Tạo calendar trên một directory các plant.</summary>
    /// <param name="directory">Nơi zone và shift table của một plant đến từ đó.</param>
    /// <param name="timeProvider">
    /// Đồng hồ, chỉ dùng cho hai method "ngay bây giờ". Được inject thay vì gọi tĩnh (K1), để một test
    /// có thể đứng ở 05:59 vào một ngày chuyển đổi mà không phải chờ đến lúc đó.
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

        // Dựng bằng cách đi tới từ thời điểm production day mở ra, không phải từ chính clock reading
        // của shift. Với shift C, bước đi đó tự vượt qua nửa đêm — 06:00 cộng mười sáu giờ là 22:00
        // ngày lịch kế tiếp — đây chính xác là phép toán mà định nghĩa production day mô tả, làm một
        // lần thay vì xử lý đặc biệt cho từng shift.
        var opensAt = day.Date.ToDateTime(schedule.DayStart);
        var startsAt = opensAt.Add(schedule.OffsetIntoDay(shift));
        var endsAt = startsAt.Add(definition.NominalLength);

        return new ShiftBoundaries(
            day,
            shift,
            Resolve(startsAt, calendar.TimeZone),
            Resolve(endsAt, calendar.TimeZone));
    }

    /// <summary>Tìm đúng một shift có boundary thật sự chứa một instant.</summary>
    /// <remarks>
    /// <para>
    /// <b>Câu trả lời được đọc từ boundary chứ không phải từ đồng hồ, và đó là cách sửa cho một mâu
    /// thuẫn có thật.</b> Đọc wall clock rồi tra reading đó trong shift table chỉ đúng khi không shift
    /// boundary nào rơi vào giờ mà một lần đổi giờ lặp lại hoặc bị bỏ qua. Bảng của NovaVolt — 06:00,
    /// 14:00, 22:00 — không bao giờ gặp trường hợp đó, nên lối tắt trông có vẻ đúng; một plant có shift
    /// đổi ca lúc 02:30 sẽ làm nó hỏng, và docs/plans §M10 mang đến đúng loại bảng đó.
    /// </para>
    /// <para>
    /// Điều đã hỏng: trong giờ bị lặp lại, cả hai lần đọc 02:00 đều tra ra cùng một shift, nhưng
    /// <see cref="Resolve"/> đặt boundary 02:30 của shift đó vào lần 02:30 <b>đầu tiên</b> có được. Nên
    /// lần 02:00 thứ hai bị <c>GetShift</c> gọi tên là một shift mà khoảng thời gian của chính nó đã
    /// kết thúc — <c>GetShift(t)</c> và <c>GetShiftBoundaries(...).Contains(t)</c> bất đồng về cùng một
    /// instant, đây chính xác là bất biến mà class này ghi lại và mọi phép đếm theo phạm vi shift phụ
    /// thuộc vào.
    /// </para>
    /// <para>
    /// Suy ra cả hai câu trả lời từ cùng các interval khiến bất biến giữ đúng nhờ cấu trúc chứ không
    /// phải nhờ trùng hợp: giờ chỉ có một quy tắc duy nhất để biến một clock reading thành một instant,
    /// và cả hai chiều đều đi qua quy tắc đó. Một ngày mỗi bên của phỏng đoán wall-clock được tìm kiếm
    /// vì một production day không phải một calendar day và một lần đổi giờ dịch chuyển boundary —
    /// không bao giờ xa hơn thế, vì không zone nào dịch đồng hồ của nó một ngày trọn vẹn.
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

                // Half-open, nên một shift bị lần đổi giờ mùa xuân ép co lại thành không còn gì sẽ
                // không chứa instant nào cả và bị bước qua thay vì được phép nhận vơ điểm bắt đầu của nó.
                if (boundaries.Contains(instant))
                {
                    return boundaries;
                }
            }
        }

        // Không thể đạt tới trong khi shift table còn phủ đồng hồ đúng một lần: các interval của các
        // production day liên tiếp gặp nhau đầu-cuối nhờ cấu trúc, nên chúng lát kín timeline. Ném lỗi
        // nghĩa là một thay đổi tương lai làm hỏng việc lát kín đó sẽ bị phát hiện ở đây, thay vì filing
        // một measurement dưới một shift mà nó không hề xảy ra trong đó.
        throw new InvalidOperationException(
            $"No shift of {calendar.SiteId} contains {instant:O}, which a validated table cannot happen to.");
    }

    /// <summary>Production day mà một clock reading của plant nêu tên, trước khi boundary tinh chỉnh nó.</summary>
    /// <remarks>
    /// Trước shift mở màn, plant vẫn đang hoàn tất chu kỳ trước. Đây là định nghĩa của một production
    /// day phát biểu theo local time — reading trên tường, vào ngày mà đồng hồ tường nói — và đó là
    /// phỏng đoán khởi điểm mà <see cref="Locate"/> tìm kiếm xung quanh, tự nó đã đúng ở mọi nơi hai
    /// lần đổi giờ trong năm không dịch chuyển một boundary.
    /// </remarks>
    private static ProductionDay WallClockDay(DateTimeOffset instant, SiteCalendar calendar)
    {
        var wallClock = WallClockAt(instant, calendar);
        var date = DateOnly.FromDateTime(wallClock);

        return TimeOnly.FromDateTime(wallClock) >= calendar.Schedule.DayStart
            ? ProductionDay.On(date)
            : ProductionDay.On(date.AddDays(-1));
    }

    /// <summary>Biến một clock reading của plant thành instant mà nó nêu tên.</summary>
    /// <remarks>
    /// <para>
    /// Một quy tắc, ba trường hợp: <b>instant sớm nhất có local clock đọc bằng hoặc sau
    /// <paramref name="wallClock"/></b>.
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// Vào một ngày bình thường thì đó chỉ đơn giản là instant có reading đó.
    /// </item>
    /// <item>
    /// Trong giờ bị lặp lại của một lần đổi giờ mùa thu, reading xảy ra hai lần, và quy tắc lấy lần
    /// <b>đầu tiên</b>. Nên shift C bắt đầu ở lần 22:00 đầu tiên có được, và giờ dư ra rơi vào bên trong
    /// shift, khiến nó dài chín giờ — đây chính là điều thực sự đã xảy ra trên sàn máy.
    /// </item>
    /// <item>
    /// Trong giờ bị bỏ qua của một lần đổi giờ mùa xuân, reading không bao giờ xảy ra, và quy tắc lấy
    /// thời điểm đồng hồ nhảy vượt qua nó.
    /// </item>
    /// </list>
    /// <para>
    /// Chọn một quy tắc duy nhất cho cả ba trường hợp là điều giữ cho <see cref="GetShift"/> và
    /// <see cref="GetShiftBoundaries"/> không mâu thuẫn nhau: một instant nằm trong boundary của một
    /// shift đúng vào lúc <c>GetShift</c> gọi tên shift đó, và điểm kết thúc của một shift chính xác là
    /// điểm bắt đầu của shift kế tiếp, không khoảng trống, không chồng lấn. Để mặc định của framework
    /// quyết định trường hợp mập mờ sẽ làm hỏng điều đó vào một ngày Chủ Nhật mỗi năm, theo cách không
    /// test thông thường nào thấy được.
    /// </para>
    /// </remarks>
    private static DateTimeOffset Resolve(DateTime wallClock, TimeZoneInfo zone)
    {
        if (zone.IsAmbiguousTime(wallClock))
        {
            // Một instant là reading trừ đi offset, nên offset lớn nhất là instant sớm nhất — lần đầu
            // tiên trong hai lần đồng hồ đọc ra giá trị này.
            return new DateTimeOffset(wallClock, zone.GetAmbiguousTimeOffsets(wallClock).Max());
        }

        if (zone.IsInvalidTime(wallClock))
        {
            return FirstInstantAtOrAfter(wallClock, zone);
        }

        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }

    /// <summary>Tìm thời điểm đồng hồ nhảy vượt qua một reading chưa từng xảy ra.</summary>
    /// <remarks>
    /// Bisection thay vì đọc <see cref="TimeZoneInfo.GetAdjustmentRules"/>, vốn có hình dạng khác nhau
    /// giữa Windows registry và IANA database và sẽ cần hai code path để ra một câu trả lời. Local
    /// time tăng nghiêm ngặt trong suốt cửa sổ này: giờ mập mờ gần nhất còn cách hàng tháng, vì không
    /// zone nào dịch đồng hồ của nó hai lần trong vòng hai ngày.
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
