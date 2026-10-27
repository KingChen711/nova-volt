using System.Collections.Frozen;

namespace Nvm.Time;

/// <summary>Một directory được xây từ một danh sách plant tường minh.</summary>
/// <remarks>
/// <para>
/// Dùng cho test, và cho một host chưa có factory model nào để đọc. Đây cố tình là thứ mà caller phải
/// <b>tự khởi tạo</b> với các plant được nêu tên từng cái một: đường đi production giải quyết zone của
/// một plant từ factory model
/// (<c>Nvm.FactoryModel.Time.FactoryModelSiteCalendarDirectory</c>), vì đó là nơi duy nhất một plant
/// mới được thêm vào và do đó là nơi duy nhất câu trả lời không thể trở nên lỗi thời.
/// </para>
/// <para>
/// Nếu type này từng xuất hiện trong composition root của một host cạnh một factory model đã load, đó
/// chính là bảng tra cứu thứ hai mà thiết kế này tồn tại để tránh.
/// </para>
/// </remarks>
public sealed class InMemorySiteCalendarDirectory : ISiteCalendarDirectory
{
    private readonly FrozenDictionary<string, SiteCalendar> _calendars;

    /// <summary>Xây một directory trên một tập plant cố định.</summary>
    /// <param name="calendars">Các plant mà directory này biết.</param>
    /// <exception cref="ArgumentException">Hai entry nêu tên cùng một plant.</exception>
    public InMemorySiteCalendarDirectory(IEnumerable<SiteCalendar> calendars)
    {
        ArgumentNullException.ThrowIfNull(calendars);

        _calendars = calendars.ToFrozenDictionary(calendar => calendar.SiteId, StringComparer.Ordinal);
    }

    /// <summary>Xây một directory trên một tập plant cố định.</summary>
    public InMemorySiteCalendarDirectory(params SiteCalendar[] calendars)
        : this((IEnumerable<SiteCalendar>)calendars)
    {
    }

    /// <inheritdoc />
    public SiteCalendar? Find(string siteId) =>
        siteId is null ? null : _calendars.GetValueOrDefault(siteId);
}
