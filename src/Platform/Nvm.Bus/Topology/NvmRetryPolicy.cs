namespace Nvm.Bus.Topology;

/// <summary>Một message thất bại được retry bao nhiêu lần, và khoảng cách giữa các lần thử là bao lâu.</summary>
/// <remarks>
/// <para>
/// Phần lớn lỗi consumer là tạm thời (transient) — một database đang kết nối lại, một lock bị giữ
/// trong chốc lát, một broker khựng lại. Retry không tốn gì và sửa được những lỗi đó. Những lỗi không
/// tạm thời phải dừng lại ở đâu đó có thể đọc được thay vì lặp mãi mãi, và đó chính là việc của error
/// queue.
/// </para>
/// <para>
/// Retry ở đây diễn ra <b>bên trong một lần delivery</b>: message không được trả về broker giữa các
/// lần thử, nên một consumer có giới hạn concurrency bị chiếm dụng suốt cả chuỗi đó. Đó là lý do các
/// khoảng chờ được đo bằng hàng trăm mili-giây và tổng thời gian ở dưới vài giây. Chờ hàng phút thuộc
/// về scheduled redelivery, thứ mà hệ thống này không có — xem <c>Nvm.Bus/README.md</c>.
/// </para>
/// </remarks>
public static class NvmRetryPolicy
{
    /// <summary>
    /// Tổng số lần một consumer chạy cho một message trước khi message đó bị đánh dấu lỗi (faulted).
    /// </summary>
    /// <remarks>
    /// Là số lần chạy (attempts), không phải số lần retry. Lần chạy đầu tiên đã là một trong năm lần
    /// đó, nên có bốn khoảng chờ giữa chúng — đây là lỗi lệch-một (off-by-one) biến "retry 5 lần" thành
    /// sáu lần consumer thực thi nếu không ai nói rõ ý nào được dùng.
    /// </remarks>
    public const int MaxAttempts = 5;

    /// <summary>Tỉ lệ mà mỗi khoảng chờ được kéo dài hoặc rút ngắn một cách ngẫu nhiên.</summary>
    public const double JitterFraction = 0.25;

    /// <summary>Khoảng chờ trước lần retry đầu tiên. Nhân đôi dần từ đó.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Trần cho bất kỳ khoảng chờ đơn lẻ nào, dù việc nhân đôi đã đi xa tới đâu.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Các khoảng chờ giữa những lần thử: theo hàm mũ (exponential), có jitter cộng thêm bằng tay.
    /// </summary>
    /// <param name="random">Nguồn phát số ngẫu nhiên. Truyền một instance có seed để test lặp lại được.</param>
    /// <remarks>
    /// <para>
    /// <c>scope.md</c> §9/M1 yêu cầu "exponential + jitter". MassTransit 8 cung cấp <c>Immediate</c>,
    /// <c>Interval</c>, <c>Intervals</c>, <c>Exponential</c> và <c>Incremental</c> — và <b>không cái
    /// nào trong số đó có tùy chọn jitter</b>. Vì vậy chuỗi này được tính toán ở đây rồi giao đi dưới
    /// dạng các khoảng cố định.
    /// </para>
    /// <para>
    /// Jitter không phải là trang trí. Không có nó, mọi consumer thất bại vì cùng một lý do chung —
    /// broker tạm dừng, database failover — sẽ retry cùng nhịp với nhau, và làn sóng retry đó ập tới
    /// đúng vào lúc hệ thống ít khả năng chịu đựng nhất. Trải các khoảng chờ ra phá vỡ sự đồng bộ đó.
    /// </para>
    /// <para>
    /// <b>Giới hạn đã biết:</b> giá trị này được tính một lần duy nhất trong lúc bus đang được xây
    /// dựng, nên jitter khác nhau giữa các tiến trình nhưng không khác nhau giữa các message bên trong
    /// một tiến trình. Điều đó bao phủ đúng trường hợp quan trọng — nhiều instance service cùng hồi
    /// phục sau cùng một sự cố — nhưng không bao phủ trường hợp một instance duy nhất đang retry hàng
    /// nghìn message cùng lúc. Jitter theo từng message cần một <c>IRetryPolicy</c> tùy biến, phức tạp
    /// hơn mức milestone này xứng đáng có.
    /// </para>
    /// </remarks>
    public static TimeSpan[] Intervals(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        var intervals = new TimeSpan[MaxAttempts - 1];
        var delay = FirstDelay;

        for (var index = 0; index < intervals.Length; index++)
        {
            // Đối xứng quanh khoảng chờ cơ sở: đôi khi sớm hơn, đôi khi muộn hơn. Nếu chỉ luôn kéo dài
            // nó thì sẽ âm thầm biến lần retry đầu 200 ms thành 250 ms.
            var offset = ((random.NextDouble() * 2) - 1) * JitterFraction;

            intervals[index] = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (1 + offset));

            delay = delay < MaxDelay
                ? TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, MaxDelay.TotalMilliseconds))
                : MaxDelay;
        }

        return intervals;
    }
}
