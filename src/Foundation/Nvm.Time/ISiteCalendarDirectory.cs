namespace Nvm.Time;

/// <summary>Nơi production calendar lấy zone và shift table của một plant.</summary>
/// <remarks>
/// <para>
/// Một port, một cách cố ý. Nguồn thẩm quyền cho việc một plant chạy theo zone nào là factory model
/// (<c>FactorySite.TimeZoneId</c>), và factory model là một Functional Block — nên adapter đọc nó nằm
/// ở đó và phụ thuộc vào assembly này, không bao giờ ngược lại. Đặt một
/// <c>Dictionary&lt;string, TimeZoneInfo&gt;</c> ở đây thay vào đó sẽ là một bảng tra cứu thứ hai, và
/// một bảng tra cứu thứ hai là một bảng sẽ lệch: ai thêm plant thứ ba ở M10 sẽ sửa factory model và
/// không có lý do gì để biết file này tồn tại.
/// </para>
/// <para>
/// Đây cũng là điều giúp calendar test được ở mức unit. Một test DST cần một zone và một bảng, không
/// cần database hay seed file.
/// </para>
/// </remarks>
public interface ISiteCalendarDirectory
{
    /// <summary>Calendar của plant, hoặc null khi directory này không biết plant đó.</summary>
    /// <param name="siteId">Mã plant, ví dụ <c>DE1</c>.</param>
    /// <remarks>
    /// Null thay vì một giá trị mặc định. Một plant không ai cấu hình không phải một plant chạy UTC
    /// với ba shift tám giờ — đó là một câu hỏi hệ thống không trả lời được, và vẫn trả lời nó là cách
    /// một site mới âm thầm báo cáo sản lượng dưới sai ngày trong suốt một tháng.
    /// </remarks>
    SiteCalendar? Find(string siteId);
}
