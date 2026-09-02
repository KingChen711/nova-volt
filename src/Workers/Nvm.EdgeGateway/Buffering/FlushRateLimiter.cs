namespace Nvm.EdgeGateway.Buffering;

/// <summary>Giới hạn tốc độ một gateway đang phục hồi được phép đẩy backlog của nó tới ingestion.</summary>
/// <remarks>
/// <para>
/// Dùng token bucket thay vì sleep cố định giữa các batch: bucket cho phép một gateway đã idle
/// gửi ngay một burst — trường hợp bình thường, nơi không có gì cần bảo vệ ai khỏi — trong khi
/// một gateway đang xả cạn hàng giờ buffer sẽ ổn định về tốc độ bền vững (sustained rate).
/// </para>
/// <para>
/// Limiter được thiết kế có thể bật/tắt một cách cố ý. Lab §5.C10.3 của plan M2 xả cạn cùng một
/// buffer y hệt hai lần, một lần tắt và một lần bật, vì một rate limit chưa từng được đo đối chiếu
/// với chính việc không có nó chỉ là một pattern, không phải một quyết định (ADR-029).
/// </para>
/// </remarks>
public sealed class FlushRateLimiter
{
    private readonly double _messagesPerSecond;
    private readonly double _capacity;
    private readonly TimeProvider _clock;
    private readonly Lock _bucket = new();
    private double _tokens;
    private long _lastRefillTimestamp;

    /// <summary>Tạo limiter theo mô tả trong buffer options.</summary>
    /// <param name="options">Cấu hình nhịp độ (pacing) flush.</param>
    /// <param name="clock">Đồng hồ điều khiển việc refill; một fake clock giúp pacing kiểm thử được (K1).</param>
    public FlushRateLimiter(PersistentBufferOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _messagesPerSecond = options.FlushMessagesPerSecond;
        _capacity = Math.Max(options.FlushBurstMessages, 1);
        _clock = clock;
        _tokens = _capacity;
        _lastRefillTimestamp = clock.GetTimestamp();
    }

    /// <summary>Có đang áp một sustained rate nào hay không.</summary>
    public bool IsEnabled => _messagesPerSecond > 0;

    /// <summary>Đợi tới khi <paramref name="messages"/> được phép gửi, rồi tiêu token.</summary>
    /// <param name="messages">Các message caller sắp POST.</param>
    /// <param name="cancellationToken">Dừng việc chờ khi host shutdown.</param>
    /// <returns>Thời gian caller bị giữ lại; <see cref="TimeSpan.Zero"/> khi không bị giữ.</returns>
    /// <remarks>
    /// Con trỏ buffer chỉ có đúng một consumer, nên hàm này được gọi tuần tự và việc tính toán là
    /// chính xác. Các caller chạy đồng thời vẫn sẽ mỗi caller đều bị pace, nhưng hai caller đó có
    /// thể cùng sleep cho cùng một khoản thiếu hụt và cùng vượt quá trong một window.
    /// </remarks>
    public async ValueTask<TimeSpan> AcquireAsync(int messages, CancellationToken cancellationToken)
    {
        if (!IsEnabled || messages <= 0)
        {
            return TimeSpan.Zero;
        }

        // Một batch lớn hơn bucket vẫn phải được cho qua, nếu không flusher sẽ đứng khựng mãi mãi
        // trên một record nó không bao giờ đủ khả năng chi trả. Tính phí toàn bộ bucket giữ cho
        // mức trung bình dài hạn chính xác.
        var cost = Math.Min(messages, _capacity);
        var waited = TimeSpan.Zero;

        while (true)
        {
            TimeSpan delay;

            lock (_bucket)
            {
                Refill();

                if (_tokens >= cost)
                {
                    _tokens -= cost;
                    return waited;
                }

                delay = TimeSpan.FromSeconds((cost - _tokens) / _messagesPerSecond);
            }

            await Task.Delay(delay, _clock, cancellationToken);
            waited += delay;
        }
    }

    private void Refill()
    {
        var now = _clock.GetTimestamp();
        var elapsed = _clock.GetElapsedTime(_lastRefillTimestamp, now);
        _lastRefillTimestamp = now;
        _tokens = Math.Min(_capacity, _tokens + (elapsed.TotalSeconds * _messagesPerSecond));
    }
}
