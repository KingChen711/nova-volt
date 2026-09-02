namespace Nvm.Sparkplug;

/// <summary>Một metric của một device, được decode ra từ một Sparkplug payload.</summary>
/// <param name="MetricName">
/// Tên đầy đủ của metric, ví dụ <c>Formation/Voltage</c>. Luôn có mặt, kể cả khi payload chỉ mang
/// alias — resolve alias thành tên chính là việc của <see cref="MetricAliasTable"/>.
/// </param>
/// <param name="Alias">Con số mà metric này được truyền dưới dạng, hoặc null nếu không có.</param>
/// <param name="Value">Giá trị đã đo được.</param>
/// <param name="DeviceTimestamp">
/// Thời điểm mà <b>device</b> nói rằng nó lấy reading. Không phải lúc gateway nhận, cũng không phải
/// lúc ta lưu — đó là hai cột khác, và docs/scope.md §7.3 nhấn mạnh rằng ba mốc thời gian này không
/// bao giờ được gộp lại.
/// </param>
/// <remarks>
/// <para>
/// <see cref="DateTimeOffset"/>, không bao giờ dùng <see cref="DateTime"/> (AGENTS.md K2). Site DE1
/// theo daylight saving, nên mỗi mùa thu có một giờ xảy ra hai lần; một reading kiểu wall-clock không
/// có offset thì không thể nói được đó là lần nào trong hai lần, và telemetry được giữ 400 ngày — đủ
/// lâu để giờ tháng Mười đó vẫn còn trong bảng khi có ai đó điều tra lại.
/// </para>
/// <para>
/// Các analyzer không bao quát trường hợp này. NVM002 từ chối <see cref="DateTime"/> trong phạm vi
/// <c>Nvm.Contracts</c>, còn type này lại nằm trong <c>Nvm.Sparkplug</c>, nên lưới chắn ở đây là
/// <c>SparkplugTimeTypeTests</c> thay vào đó.
/// </para>
/// <para>
/// Một reading cố tình <b>không phải</b> là một event. Nó chưa có <c>SiteId</c> và chưa có equipment
/// path, vì Sparkplug payload không mang hai thứ đó — chúng nằm trong MQTT topic, và C03 là bước ghép
/// hai thứ lại với nhau.
/// </para>
/// </remarks>
public sealed record DeviceReading(
    string MetricName,
    ulong? Alias,
    MetricValue Value,
    DateTimeOffset DeviceTimestamp);
