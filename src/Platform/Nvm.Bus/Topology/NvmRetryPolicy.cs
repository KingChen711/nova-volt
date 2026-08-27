namespace Nvm.Bus.Topology;

/// <summary>How many times a failing message is retried, and how long between attempts.</summary>
/// <remarks>
/// <para>
/// Most consumer failures are transient — a database reconnecting, a lock held for a moment, a
/// broker hiccup. Retrying costs nothing and fixes them. The failures that are not transient must
/// stop somewhere readable rather than loop forever, which is what the error queue is for.
/// </para>
/// <para>
/// Retries here happen <b>inside one delivery</b>: the message is not returned to the broker between
/// attempts, so a consumer with a concurrency limit is occupied for the whole sequence. That is why
/// the delays are measured in hundreds of milliseconds and the total stays under a few seconds.
/// Waiting minutes belongs to scheduled redelivery, which this system does not have — see
/// <c>Nvm.Bus/README.md</c>.
/// </para>
/// </remarks>
public static class NvmRetryPolicy
{
    /// <summary>
    /// Total times a consumer runs for one message before the message is faulted.
    /// </summary>
    /// <remarks>
    /// Attempts, not retries. The first run is one of the five, so there are four waits between them
    /// — the off-by-one that turns "retry 5 times" into six consumer executions if nobody says which
    /// is meant.
    /// </remarks>
    public const int MaxAttempts = 5;

    /// <summary>Fraction by which each interval is randomly stretched or shortened.</summary>
    public const double JitterFraction = 0.25;

    /// <summary>Wait before the first retry. Doubles from there.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Ceiling on any single wait, however far the doubling has gone.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The waits between attempts: exponential, with jitter added by hand.
    /// </summary>
    /// <param name="random">Source of randomness. Pass a seeded instance to make a test repeatable.</param>
    /// <remarks>
    /// <para>
    /// <c>scope.md</c> §9/M1 asks for "exponential + jitter". MassTransit 8 offers <c>Immediate</c>,
    /// <c>Interval</c>, <c>Intervals</c>, <c>Exponential</c> and <c>Incremental</c> — and <b>no jitter
    /// option on any of them</b>. So the sequence is computed here and handed over as fixed intervals.
    /// </para>
    /// <para>
    /// Jitter is not decoration. Without it, every consumer that failed for one shared reason — the
    /// broker paused, the database failed over — retries in lockstep, and the retry wave lands
    /// together at exactly the moment the system is least able to absorb it. Spreading the waits
    /// breaks the synchronisation.
    /// </para>
    /// <para>
    /// <b>Known limit:</b> this is computed once while the bus is being built, so the jitter differs
    /// between processes but not between messages inside one process. That covers the case that
    /// matters — several service instances recovering from the same outage — and does not cover a
    /// single instance retrying a thousand messages at once. Per-message jitter needs a custom
    /// <c>IRetryPolicy</c>, which is more machinery than this milestone earns.
    /// </para>
    /// </remarks>
    public static TimeSpan[] Intervals(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        var intervals = new TimeSpan[MaxAttempts - 1];
        var delay = FirstDelay;

        for (var index = 0; index < intervals.Length; index++)
        {
            // Symmetric around the base delay: sometimes sooner, sometimes later. Only ever stretching
            // it would quietly turn a 200 ms first retry into a 250 ms one.
            var offset = ((random.NextDouble() * 2) - 1) * JitterFraction;

            intervals[index] = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (1 + offset));

            delay = delay < MaxDelay
                ? TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, MaxDelay.TotalMilliseconds))
                : MaxDelay;
        }

        return intervals;
    }
}
