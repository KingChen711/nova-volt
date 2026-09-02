namespace Nvm.FactoryModel.Entities;

/// <summary>Một plant, cùng với các thuộc tính thuộc về plant chứ không thuộc về một node.</summary>
/// <param name="SiteId">Mã code mà mọi record đều mang theo, ví dụ <c>NV1</c>.</param>
/// <param name="Name">Tên hiển thị.</param>
/// <param name="TimeZoneId">
/// Time zone theo IANA, ví dụ <c>Asia/Ho_Chi_Minh</c> hoặc <c>Europe/Berlin</c>.
/// </param>
/// <param name="Root">Node của site, và thông qua nó là mọi thứ bên dưới.</param>
/// <remarks>
/// <para>
/// Tách riêng khỏi <see cref="FactoryNode"/> vì time zone không phải là một thuộc tính của node — một
/// stacker thì không có time zone. Gắn nó lên mọi node sẽ đặt một field nullable lên bốn mươi node chỉ
/// để hai trong số đó thực sự dùng đến.
/// </para>
/// <para>
/// Time zone không phải là trang trí. Các ca chạy 06–14, 14–22 và 22–06 theo giờ local, và một
/// production day là ngày dương lịch mà ca A của chu kỳ đó bắt đầu, nên một ca đêm thuộc về ngày mà nó
/// bắt đầu chạy. <c>DE1</c> áp dụng daylight saving, khiến ca C của nó dài chín giờ vào một lần trong
/// năm và ngắn bảy giờ vào một lần khác trong năm — chính là lý do site này tồn tại trong model
/// (docs/scope.md §2.3).
/// </para>
/// </remarks>
public sealed record FactorySite(string SiteId, string Name, string TimeZoneId, FactoryNode Root);
