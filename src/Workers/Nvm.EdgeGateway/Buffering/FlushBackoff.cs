namespace Nvm.EdgeGateway.Buffering;

/// <summary>Flusher chờ bao lâu sau một lần POST thất bại, và vì sao khoảng chờ đó lớn dần.</summary>
/// <remarks>
/// <para>
/// Cùng lý lẽ như <c>NvmRetryPolicy</c> ở M1: cả một khu của nhà máy mất mạng cùng lúc và có
/// mạng lại cùng lúc, nên backoff giống hệt nhau nghĩa là mốc retry giống hệt nhau — một retry
/// storm nhắm thẳng vào hệ thống đúng lúc nó ít khả năng chịu đựng nhất.
/// </para>
/// <para>
/// Một khác biệt cố ý so với <c>NvmRetryPolicy</c>: jitter ở đây chỉ luôn nới dài khoảng chờ,
/// không bao giờ rút ngắn. Ingestion có thể gửi kèm một sàn <c>Retry-After</c>, và jitter đối
/// xứng sẽ khiến gateway quay lại sớm hơn đúng cái mốc server vừa nói là nó chịu được. Trải
/// client lệch lên trên vẫn phá được đồng pha y hệt, và không thể vi phạm cái sàn đó.
/// </para>
/// </remarks>
public sealed class FlushBackoff
{
    private readonly TimeSpan _firstDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterFraction;
    private readonly Random _random;

    /// <summary>Tạo lịch trình được mô tả bởi buffer options.</summary>
    /// <param name="options">Cấu hình flush retry.</param>
    /// <param name="random">Nguồn jitter; truyền một instance có seed để test có thể lặp lại được.</param>
    public FlushBackoff(PersistentBufferOptions options, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);

        _firstDelay = options.FlushRetryDelay;
        _maxDelay = options.FlushRetryMaxDelay;
        _jitterFraction = options.FlushRetryJitterFraction;
        _random = random;
    }

    /// <summary>Tính khoảng chờ trước lần thử kế tiếp.</summary>
    /// <param name="consecutiveFailures">Số lần thất bại kể từ lần thành công gần nhất; 1 cho lần đầu.</param>
    /// <param name="serverHint">Giá trị <c>Retry-After</c> mà ingestion cung cấp, nếu có.</param>
    /// <returns>Một khoảng chờ đã áp jitter, không bao giờ nhỏ hơn <paramref name="serverHint"/>.</returns>
    public TimeSpan NextDelay(long consecutiveFailures, TimeSpan? serverHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailures);

        // Việc nhân đôi được tính qua số mũ thay vì nhân lặp lại, để một flusher đã thất bại
        // hàng giờ liền không làm tràn (overflow) khoảng chờ của chính nó thành một con số vô lý.
        var doublings = Math.Min(consecutiveFailures - 1, 32);
        var exponential = _firstDelay.TotalMilliseconds * Math.Pow(2, doublings);
        var target = Math.Min(exponential, _maxDelay.TotalMilliseconds);

        if (serverHint is { } hint && hint.TotalMilliseconds > target)
        {
            target = hint.TotalMilliseconds;
        }

        var stretch = 1 + (_random.NextDouble() * _jitterFraction);
        return TimeSpan.FromMilliseconds(target * stretch);
    }
}
