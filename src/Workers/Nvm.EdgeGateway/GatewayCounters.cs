namespace Nvm.EdgeGateway;

/// <summary>Process counter xoay quanh việc chấp nhận MQTT và forwarding durable.</summary>
public sealed class GatewayCounters
{
    private long _decodedMessages;
    private long _bufferedMessages;
    private long _forwardedMessages;
    private long _rejectedMessages;
    private long _bufferFullEvents;
    private long _throttledFlushes;
    private long _rateLimitedFlushes;
    private long _rebirthRequests;
    private long _lateDeathsIgnored;
    private long _flushBatches;

    /// <summary>Message birth/data đã decode thành công.</summary>
    public long DecodedMessages => Interlocked.Read(ref _decodedMessages);

    /// <summary>Message đã fsync trước khi acknowledge MQTT.</summary>
    public long BufferedMessages => Interlocked.Read(ref _bufferedMessages);

    /// <summary>Message được ingestion chấp nhận và đã được durable cursor đi qua.</summary>
    public long ForwardedMessages => Interlocked.Read(ref _forwardedMessages);

    /// <summary>Message sai định dạng hoặc site không xác định, bị từ chối trước khi forward.</summary>
    public long RejectedMessages => Interlocked.Read(ref _rejectedMessages);

    /// <summary>Số lần trần đĩa cứng chặn việc chấp nhận MQTT.</summary>
    public long BufferFullEvents => Interlocked.Read(ref _bufferFullEvents);

    /// <summary>Số lần ingestion trả lời 429/503 và gateway chậm lại thay vì retry.</summary>
    public long ThrottledFlushes => Interlocked.Read(ref _throttledFlushes);

    /// <summary>Số lần rate limiter của chính gateway giữ một batch lại.</summary>
    /// <remarks>
    /// Được tách riêng khỏi <see cref="ThrottledFlushes"/> vì lab §5.C10.3 tồn tại để phân biệt hai
    /// nguyên nhân: "ta tự pace mình" và "server bắt ta phải pace" tạo ra cùng một quá trình xả cạn
    /// chậm nhưng dẫn tới kết luận hoàn toàn đối lập về việc ADR-029 có xứng đáng tồn tại hay không.
    /// </remarks>
    public long RateLimitedFlushes => Interlocked.Read(ref _rateLimitedFlushes);

    /// <summary>Số lần gateway yêu cầu một node khai báo lại chính nó.</summary>
    /// <remarks>
    /// Một sequence gap nghĩa là dữ liệu đã bị bỏ lỡ, và một message bị bỏ lỡ có thể chính là
    /// message đã đánh số lại một alias. Con số này là bằng chứng cho C17: một lần đối soát ra kết
    /// quả khớp với zero lần rebirth trên một run có bật dropout thì chưa thực sự thực thi con
    /// đường mà nó tuyên bố.
    /// </remarks>
    public long RebirthRequests => Interlocked.Read(ref _rebirthRequests);

    /// <summary>Death bị từ chối vì chúng nêu tên một session đã bị thay thế.</summary>
    public long LateDeathsIgnored => Interlocked.Read(ref _lateDeathsIgnored);

    /// <summary>Batch mà ingestion đã chấp nhận và durable cursor đã đi qua.</summary>
    /// <remarks>
    /// Flusher được thiết kế là một vòng lặp tuần tự duy nhất: đọc, POST, đẩy cursor tiến lên, lặp
    /// lại. Vì vậy trần của nó là batch size chia cho round trip, và forwarded/batches là con số
    /// duy nhất cho biết một quá trình xả cạn chậm đang thiếu ở cái nào trong hai cái đó.
    /// </remarks>
    public long FlushBatches => Interlocked.Read(ref _flushBatches);

    internal long CountDecoded() => Interlocked.Increment(ref _decodedMessages);

    internal long CountBuffered(int count = 1) => Interlocked.Add(ref _bufferedMessages, count);

    internal long CountForwarded(int count = 1) => Interlocked.Add(ref _forwardedMessages, count);

    internal long CountRejected() => Interlocked.Increment(ref _rejectedMessages);

    internal long CountBufferFull() => Interlocked.Increment(ref _bufferFullEvents);

    internal long CountThrottled() => Interlocked.Increment(ref _throttledFlushes);

    internal long CountRateLimited() => Interlocked.Increment(ref _rateLimitedFlushes);

    internal long CountRebirthRequested() => Interlocked.Increment(ref _rebirthRequests);

    internal long CountLateDeathIgnored() => Interlocked.Increment(ref _lateDeathsIgnored);

    internal long CountFlushBatch() => Interlocked.Increment(ref _flushBatches);
}
