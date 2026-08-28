namespace Nvm.EdgeGateway.Buffering;

/// <summary>Caps how fast a recovering gateway may push its backlog at ingestion.</summary>
/// <remarks>
/// <para>
/// A token bucket rather than a fixed sleep between batches: the bucket lets a gateway that has
/// been idle send one burst immediately — the normal case, where there is nothing to protect
/// anyone from — while a gateway draining hours of buffer settles onto the sustained rate.
/// </para>
/// <para>
/// The limiter is deliberately switchable. Lab §5.C10.3 of the M2 plan drains one identical
/// buffer twice, once with it off and once with it on, because a rate limit that has never been
/// measured against its own absence is a pattern, not a decision (ADR-029).
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

    /// <summary>Creates the limiter described by the buffer options.</summary>
    /// <param name="options">Flush pacing configuration.</param>
    /// <param name="clock">Clock driving refill; a fake clock makes the pacing testable (K1).</param>
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

    /// <summary>Whether a sustained rate is being enforced at all.</summary>
    public bool IsEnabled => _messagesPerSecond > 0;

    /// <summary>Waits until <paramref name="messages"/> may be sent, then spends the tokens.</summary>
    /// <param name="messages">Messages the caller is about to POST.</param>
    /// <param name="cancellationToken">Stops the wait when the host shuts down.</param>
    /// <returns>How long the caller was held back; <see cref="TimeSpan.Zero"/> when it was not.</returns>
    /// <remarks>
    /// The buffer cursor has exactly one consumer, so this is called serially and the accounting is
    /// exact. Concurrent callers would still each be paced, but two of them could sleep for the
    /// same deficit and jointly overshoot for one window.
    /// </remarks>
    public async ValueTask<TimeSpan> AcquireAsync(int messages, CancellationToken cancellationToken)
    {
        if (!IsEnabled || messages <= 0)
        {
            return TimeSpan.Zero;
        }

        // A batch larger than the bucket must still pass, or the flusher would stall forever on a
        // record it can never afford. Charging the full bucket keeps the long-run average honest.
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
