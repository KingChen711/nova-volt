namespace Nvm.EdgeGateway.Buffering;

/// <summary>Giới hạn vật lý và cách gộp fsync cho hàng đợi store-and-forward ở edge.</summary>
public sealed class PersistentBufferOptions
{
    /// <summary>Thư mục chứa các file segment và cursor.</summary>
    public string DirectoryPath { get; set; } = "/var/lib/nvm-edge-gateway/buffer";

    /// <summary>Rotate trước khi một record mới sẽ đẩy một segment không rỗng vượt quá kích thước này.</summary>
    public long SegmentBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Dừng nhận dữ liệu mới khi data-file đạt tới số byte này.</summary>
    public long MaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Từ chối một record lớn hơn giá trị này trước khi cấp phát hoặc ghi nó.</summary>
    public int MaxRecordBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Số record tối đa chia sẻ chung một fsync của data-file.</summary>
    /// <remarks>
    /// Cố tình để ở mức 128. Batch size nhân với tần suất fsync trông như đáng lẽ phải là trần
    /// ingest, và batch quả thực chạy bão hòa ở mức ~127 trên 128 record - nhưng nâng lên 1024 lại
    /// đo được chậm hơn một chút (4.931 so với 5.104 msg/s vào ngày 2026-08-29), vì một batch rộng
    /// hơn chỉ đơn giản là chờ lâu hơn để đầy. Trần nằm ở phía trước ổ đĩa; xem benchmarks.md.
    /// </remarks>
    public int FsyncBatchSize { get; set; } = 128;

    /// <summary>Thời gian tối đa (wall time) một MQTT publish đã được chấp nhận phải chờ một fsync batch.</summary>
    public TimeSpan FsyncInterval { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Số record tối đa một lần flush đọc cùng lúc.</summary>
    public int FlushBatchSize { get; set; } = 1024;

    /// <summary>Số byte payload đã decode tối đa một lần flush đọc cùng lúc.</summary>
    public int FlushBatchBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Trần flush bền vững (sustained), tính bằng message mỗi giây. Zero tắt giới hạn — chính cấu
    /// hình mà lab §5.C10.3 bật/tắt để đo xem ADR-029 có mang lại lợi ích gì không.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zero, nghĩa là không có trần.</b> Giá trị mặc định trước đây là 12.000, chọn để nằm trên
    /// N1 sao cho một backlog vẫn có thể được bắt kịp — một con số nghe có vẻ hợp lý được chọn
    /// trước khi có bất kỳ dữ liệu nào. Lab #3 sau đó đo được pipeline xả cạn ở <b>13.224 msg/s</b>,
    /// nghĩa là trần đó nằm DƯỚI năng lực thực tế và trở thành thứ duy nhất quyết định một lần phục
    /// hồi diễn ra nhanh cỡ nào. Nó tốn thêm 165 giây trên một backlog ba mươi phút (+24%) và không
    /// ngăn được gì cả: ingestion chưa từng trả về dù chỉ một lần 429 hay 503 ở cả hai nhánh, và
    /// run outage của D3 cũng ghi nhận <c>rate_limited = 0</c>.
    /// </para>
    /// <para>
    /// Cơ chế bảo vệ thực sự hiệu quả là vòng lặp khép kín: ingestion báo nó đang quá tải bằng 429
    /// hay 503 kèm <c>Retry-After</c>, và flusher giảm tốc xuống mức sàn đó. Đó là câu trả lời cho
    /// một tín hiệu thật. Một trần tĩnh chỉ là câu trả lời cho một phỏng đoán, và một phỏng đoán
    /// phải được đặt lại mỗi khi phần cứng thay đổi là một hàng rào không ai có thể tin tưởng.
    /// </para>
    /// <para>
    /// Vẫn là một knob, và lab #3 chính là bài A/B dùng đến nó. Nếu M13 phát hiện ra rằng nhiều
    /// gateway cùng kết nối lại một lúc làm quá tải ingestion khi cộng gộp — điều mà một gateway
    /// đơn lẻ không thể chứng minh được — thì con số đặt ở đây phải đến từ một năng lực cộng gộp đã
    /// được đo đạc, không phải từ một phỏng đoán khác. Xem ADR-029.
    /// </para>
    /// </remarks>
    public int FlushMessagesPerSecond { get; set; }

    /// <summary>Số message một gateway đang idle được phép gửi trong một burst trước khi sustained rate áp dụng.</summary>
    public int FlushBurstMessages { get; set; } = 12_000;

    /// <summary>Thời gian chờ đầu tiên sau một POST thất bại. Tăng gấp đôi từ đó trở đi.</summary>
    public TimeSpan FlushRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Trần cho bất kỳ lần chờ retry đơn lẻ nào, bất kể ingestion đã down bao lâu.</summary>
    public TimeSpan FlushRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tỉ lệ mỗi lần chờ retry bị kéo giãn ngẫu nhiên (jitter).</summary>
    public double FlushRetryJitterFraction { get; set; } = 0.25;

    /// <summary>Sức chứa của bộ đệm handoff trong bộ nhớ đưa vào fsync writer.</summary>
    public int PendingWriteCapacity { get; set; } = 8192;

    /// <summary>Xác thực rằng mọi giới hạn vật lý đều có thể vận hành được.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DirectoryPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SegmentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRecordBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FsyncBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FlushBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FlushBatchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PendingWriteCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(FlushMessagesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(FlushRetryJitterFraction);

        if (FlushMessagesPerSecond > 0 && FlushBurstMessages <= 0)
        {
            throw new InvalidOperationException("A rate-limited flush needs a positive burst allowance.");
        }

        if (FlushRetryMaxDelay < FlushRetryDelay)
        {
            throw new InvalidOperationException("FlushRetryMaxDelay cannot be shorter than the first retry delay.");
        }

        if (SegmentBytes > MaxBytes)
        {
            throw new InvalidOperationException("A buffer segment cannot be larger than the whole buffer cap.");
        }

        if (MaxRecordBytes + FileStoreAndForwardBuffer.RecordOverhead > SegmentBytes)
        {
            throw new InvalidOperationException("MaxRecordBytes plus its framing must fit in one segment.");
        }

        if (FsyncInterval <= TimeSpan.Zero || FlushRetryDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Fsync interval and flush retry delay must be positive.");
        }
    }
}
