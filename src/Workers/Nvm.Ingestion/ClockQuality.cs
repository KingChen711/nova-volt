namespace Nvm.Ingestion;

/// <summary>Đồng hồ của thiết bị đáng tin đến mức nào cho lần đo này.</summary>
/// <remarks>
/// <para>
/// Đây là một cờ đánh dấu (flag) chứ không phải bộ lọc. Phản xạ kỹ thuật thường là từ chối dữ liệu
/// sai, nhưng áp dụng ở đây nghĩa là một cục pin CMOS hai chục nghìn đồng chết đi sẽ xoá sạch toàn bộ
/// hồ sơ truy vết (traceability) của một dòng dữ liệu — âm thầm, cho tới khi auditor hỏi tới. Đồng hồ
/// PLC trôi liên tục: pin hết, NTP không tới được tầng OT, board bị thay và khởi động lại ở giá trị
/// mặc định nhà máy.
/// </para>
/// <para>
/// Điều khiến cờ này an toàn là <c>gateway_timestamp</c> luôn tồn tại và đáng tin. Reading vẫn được
/// lưu, được đánh dấu, và hiển thị kèm badge; người vận hành sửa đồng hồ, còn hệ thống không mất gì
/// trong lúc chờ (N15).
/// </para>
/// </remarks>
public enum ClockQuality
{
    /// <summary>Đồng hồ thiết bị và gateway khớp nhau trong ngưỡng cho phép.</summary>
    Good,

    /// <summary>Chênh lệch vượt ngưỡng. Reading vẫn được giữ lại và gắn cờ.</summary>
    Drifted,

    /// <summary>Nguồn dữ liệu không có đồng hồ thiết bị nào cả, nên không có gì để so sánh.</summary>
    /// <remarks>
    /// Không thể xảy ra trên đường Sparkplug: C02 từ chối một metric không có timestamp ở bất kỳ đâu,
    /// vì <c>device_timestamp</c> là một phần của natural key (scope.md §7.2) và một reading thiếu nó
    /// sẽ không bao giờ tự nhận ra mình là bản trùng lặp. Nguồn thực sự là CSV file drop ở C15, nơi
    /// một máy test cuối chuyền (end-of-line tester) xuất dòng dữ liệu mà chưa từng có đồng hồ thiết
    /// bị nào liên quan.
    /// </remarks>
    Unknown,
}

/// <summary>So sánh đồng hồ thiết bị với đồng hồ gateway.</summary>
public static class ClockQualityClassifier
{
    /// <summary>Ngưỡng dung sai mặc định trước khi một đồng hồ thiết bị bị coi là drifted (scope.md §7.3).</summary>
    /// <remarks>
    /// Năm phút đủ rộng để độ trôi NTP thông thường và độ trễ mạng không bao giờ chạm ngưỡng, và đủ
    /// hẹp để những lỗi đáng nêu tên — một PLC chết pin lệch hàng giờ hoặc hàng năm — không thể ẩn
    /// bên trong. Đây là ngưỡng trên độ <b>chênh lệch</b>, không phải trên độ trễ: một reading bị đệm
    /// lại một giờ do store-and-forward vẫn là Good, vì hai đồng hồ vẫn đồng ý về thời điểm nó được đo.
    /// </remarks>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(5);

    /// <summary>Phân loại một reading.</summary>
    /// <param name="deviceTimestamp">Thời điểm thiết bị nói rằng nó đã đo, nếu có nói.</param>
    /// <param name="gatewayTimestamp">Thời điểm gateway nhận được publish mang reading này.</param>
    /// <param name="threshold">Hai thời điểm được lệch nhau bao xa mà vẫn coi là Good.</param>
    /// <returns>Chất lượng để lưu cùng cả ba timestamp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Threshold âm.</exception>
    public static ClockQuality Classify(
        DateTimeOffset? deviceTimestamp,
        DateTimeOffset gatewayTimestamp,
        TimeSpan threshold)
    {
        if (threshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                threshold,
                "A negative tolerance would call every reading drifted.");
        }

        if (deviceTimestamp is not { } device)
        {
            return ClockQuality.Unknown;
        }

        // Lấy giá trị tuyệt đối, để một đồng hồ chạy nhanh cũng lộ rõ như một đồng hồ chạy chậm. Nếu
        // chỉ so sánh một chiều thì một PLC đóng dấu reading vào hai giờ trong tương lai sẽ bị coi là
        // Good, trong khi đó chính là hình dạng thực tế của một board vừa được thay mới.
        var disagreement = device > gatewayTimestamp
            ? device - gatewayTimestamp
            : gatewayTimestamp - device;

        return disagreement > threshold ? ClockQuality.Drifted : ClockQuality.Good;
    }

    /// <summary>Giá trị được lưu trong <c>ts.telemetry_measurement.clock_quality</c>.</summary>
    /// <param name="quality">Kết quả phân loại.</param>
    /// <exception cref="ArgumentOutOfRangeException">Giá trị không phải một phân loại hợp lệ.</exception>
    /// <remarks>
    /// Viết tường minh ra chuỗi thay vì lấy từ <c>ToString</c>. Cột này có ràng buộc CHECK trên đúng
    /// ba cách viết này, và đổi tên một thành viên enum là một việc refactor mà không ai ngờ có thể
    /// làm gãy một lệnh ghi database.
    /// </remarks>
    public static string ToColumnValue(this ClockQuality quality) =>
        quality switch
        {
            ClockQuality.Good => "Good",
            ClockQuality.Drifted => "Drifted",
            ClockQuality.Unknown => "Unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown clock quality."),
        };
}
