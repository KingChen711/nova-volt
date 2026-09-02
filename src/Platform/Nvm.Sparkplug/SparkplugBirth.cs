using System.Collections.Immutable;

namespace Nvm.Sparkplug;

/// <summary>Một birth đã decode: những gì device đã khai báo, và cái bảng giúp các message sau đó đọc được.</summary>
/// <param name="Readings">
/// Các giá trị mà birth mang theo. Một birth không chỉ là một lời khai báo — nó còn báo cáo giá trị
/// hiện tại của mọi metric, đó là điều giúp một listener vừa mới kết nối có thể thấy ngay bức tranh
/// đầy đủ thay vì phải đợi từng giá trị thay đổi.
/// </param>
/// <param name="Aliases">Bảng alias cho session này của node này.</param>
/// <param name="Sequence">
/// <c>seq</c> của payload. Một birth mang giá trị 0, và mọi message sau đó đếm lên tiếp từ đó — đó
/// là cách một listener nhận ra mình đã bỏ lỡ một message.
/// </param>
/// <param name="BirthDeathSequence">
/// Metric <c>bdSeq</c>, hoặc null khi payload không có nó. Nó đặt tên cho <b>session nào</b> mà birth
/// này mở ra, để một <c>NDEATH</c> đến muộn từ session trước có thể phân biệt được với cái chết của
/// session đang chạy hiện tại.
/// </param>
/// <remarks>
/// Bốn kết quả từ một lần parse. Xây bảng bằng cách decode payload lần thứ hai vẫn chạy được, nhưng
/// cũng sẽ mở đường cho hai kết quả không khớp nhau — đúng kiểu bug chỉ lộ ra khi hai lần gọi đó bị
/// một refactor tách rời nhau.
/// </remarks>
public sealed record SparkplugBirth(
    ImmutableArray<DeviceReading> Readings,
    MetricAliasTable Aliases,
    ulong? Sequence,
    ulong? BirthDeathSequence);

/// <summary>Một <c>NDEATH</c> đã decode: session nào đã kết thúc, và không gì khác.</summary>
/// <param name="BirthDeathSequence">
/// <c>bdSeq</c> mà session đang chết đã được sinh ra dưới đó. Thiếu nó, một cái chết bị broker giữ
/// lại rồi giao muộn sẽ đánh dấu stale cho một node thực ra đã quay lại rồi.
/// </param>
/// <remarks>
/// Không có readings. Một death là broker nói thay cho node — node đã biến mất và không còn giá trị
/// gì để báo cáo. Bất cứ thứ gì một death có mang theo thì cũng đã cũ như chính lần ngắt kết nối đó.
/// </remarks>
public sealed record SparkplugDeath(ulong? BirthDeathSequence);
