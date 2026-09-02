namespace Nvm.Time;

/// <summary>Một plant tính giờ như thế nào: zone và shift table của nó.</summary>
/// <param name="SiteId">Plant, ví dụ <c>NV1</c>.</param>
/// <param name="TimeZone">Zone của plant, resolve từ một IANA id như <c>Europe/Berlin</c>.</param>
/// <param name="Schedule">Shift table mà plant chạy.</param>
/// <remarks>
/// Hai thứ này đi cùng nhau vì không cái nào tự trả lời được một câu hỏi. "23:47 là shift nào?" cần
/// zone để biết đồng hồ trên tường đọc ra gì và cần bảng để biết reading đó nghĩa là gì, và một plant
/// đổi một trong hai mà không đổi cái còn lại sẽ là một plant không ai có thể suy luận về được.
/// </remarks>
public sealed record SiteCalendar(string SiteId, TimeZoneInfo TimeZone, ShiftSchedule Schedule);
