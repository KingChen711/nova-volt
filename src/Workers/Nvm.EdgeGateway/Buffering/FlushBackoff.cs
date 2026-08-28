namespace Nvm.EdgeGateway.Buffering;

/// <summary>How long the flusher waits after a failed POST, and why that wait grows.</summary>
/// <remarks>
/// <para>
/// Same reasoning as <c>NvmRetryPolicy</c> in M1: a whole area of the plant loses the network at
/// once and gets it back at once, so identical backoff means identical retry instants — a retry
/// storm aimed at the system in the minute it is least able to absorb one.
/// </para>
/// <para>
/// One deliberate difference from <c>NvmRetryPolicy</c>: jitter here only ever stretches the wait,
/// never shortens it. Ingestion can send a <c>Retry-After</c> floor, and symmetric jitter would
/// let the gateway come back sooner than the server just said it could cope with. Spreading the
/// clients upward breaks the synchronisation just as well and cannot violate that floor.
/// </para>
/// </remarks>
public sealed class FlushBackoff
{
    private readonly TimeSpan _firstDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterFraction;
    private readonly Random _random;

    /// <summary>Creates the schedule described by the buffer options.</summary>
    /// <param name="options">Flush retry configuration.</param>
    /// <param name="random">Jitter source; pass a seeded instance to make a test repeatable.</param>
    public FlushBackoff(PersistentBufferOptions options, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);

        _firstDelay = options.FlushRetryDelay;
        _maxDelay = options.FlushRetryMaxDelay;
        _jitterFraction = options.FlushRetryJitterFraction;
        _random = random;
    }

    /// <summary>Computes the wait before the next attempt.</summary>
    /// <param name="consecutiveFailures">Failures since the last success; 1 for the first.</param>
    /// <param name="serverHint">A <c>Retry-After</c> value ingestion supplied, when it did.</param>
    /// <returns>A jittered delay that is never below <paramref name="serverHint"/>.</returns>
    public TimeSpan NextDelay(long consecutiveFailures, TimeSpan? serverHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailures);

        // Doubling is computed in the exponent rather than by repeated multiplication so a flusher
        // that has been failing for hours does not overflow its own delay into something absurd.
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
