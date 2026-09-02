using System.Collections.Concurrent;
using Nvm.FactoryModel.Storage;
using Nvm.Time;

namespace Nvm.FactoryModel.Time;

/// <summary>Đọc time zone của một plant từ revision factory model mà plant đó đang chạy.</summary>
/// <remarks>
/// <para>
/// Toàn bộ điểm mấu chốt của class này là <b>không có bảng tra cứu thứ hai</b>. Viết
/// <c>{"NV1": "Asia/Ho_Chi_Minh", "DE1": "Europe/Berlin"}</c> ở đâu đó chỉ tốn hai dòng và hoạt động
/// ngay hôm nay; cái giá phải trả đến ở M10, khi ai đó thêm plant thứ ba sẽ sửa factory model — nơi
/// duy nhất một plant được định nghĩa — và không có lý do gì để biết một danh sách thứ hai tồn tại.
/// Site mới khi đó sẽ tính shift của nó ở sai zone, và không có gì báo đỏ cả.
/// </para>
/// <para>
/// <b>Revision đang có hiệu lực, không phải cái mới nhất trên kệ.</b> <c>ADR-024</c> khiến một staged
/// rollout trở thành bình thường: NV1 có thể đang chạy revision 3 trong khi DE1 vẫn ở 1. Hỏi catalog
/// về document mới nhất sẽ cho DE1 một câu trả lời từ một document mà DE1 chưa hề áp dụng.
/// </para>
/// <para>
/// Adapter này chính là lý do dependency chạy theo chiều Functional Block → <c>Nvm.Time</c> và không
/// bao giờ ngược lại. <c>Nvm.Time</c> nêu ra câu hỏi (<see cref="ISiteCalendarDirectory"/>); block sở
/// hữu cây plant trả lời nó.
/// </para>
/// </remarks>
public sealed class FactoryModelSiteCalendarDirectory : ISiteCalendarDirectory
{
    // Resolve một id IANA phải duyệt qua zone database, và cái này được hỏi một lần mỗi phép đo trên
    // một số đường. Đánh khóa theo id thay vì theo plant, để activate một revision mới đổi zone của
    // một plant có hiệu lực ngay ở lần gọi kế tiếp mà không có invalidation nào để làm sai.
    private readonly ConcurrentDictionary<string, TimeZoneInfo> _zones = new(StringComparer.Ordinal);
    private readonly IActiveFactoryModel _active;

    /// <summary>Tạo directory dựa trên bất kỳ điều gì mỗi plant hiện đang activate.</summary>
    /// <param name="active">Revision nào đang có hiệu lực ở mỗi plant.</param>
    public FactoryModelSiteCalendarDirectory(IActiveFactoryModel active) =>
        _active = active ?? throw new ArgumentNullException(nameof(active));

    /// <inheritdoc />
    /// <remarks>
    /// Null bao trùm hai tình huống khác nhau, và cả hai đều thực sự là "không có calendar": model
    /// không chứa plant đó, và plant chưa activate revision nào cả. Không trường hợp nào là lý do để
    /// đoán một zone — một plant chưa ai bật lên thì không có shift nào để báo cáo dựa vào.
    /// </remarks>
    public SiteCalendar? Find(string siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return null;
        }

        var current = _active.Current(siteId);

        if (current is null)
        {
            return null;
        }

        // Một bảng dùng chung cho mọi plant, tính đến hôm nay. Bảng shift thuộc về factory model, đi
        // cạnh zone, và M10 là milestone đưa nó vào đó — chỗ nối này ở đây để việc thêm nó chỉ là một
        // thay đổi trên dòng này thay vì trên mọi caller.
        return new SiteCalendar(
            current.SiteId,
            _zones.GetOrAdd(current.Site.TimeZoneId, SiteTimeZone.Of),
            ShiftSchedule.Default);
    }
}
