namespace Nvm.EdgeGateway.Buffering;

/// <summary>Physical limits and fsync batching for the edge store-and-forward queue.</summary>
public sealed class PersistentBufferOptions
{
    /// <summary>Directory holding segment and cursor files.</summary>
    public string DirectoryPath { get; set; } = "/var/lib/nvm-edge-gateway/buffer";

    /// <summary>Rotate before a new record would take a non-empty segment past this size.</summary>
    public long SegmentBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Stop accepting new data at this many data-file bytes.</summary>
    public long MaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Reject one record larger than this before allocating or writing it.</summary>
    public int MaxRecordBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Maximum records sharing one data-file fsync.</summary>
    /// <remarks>
    /// Left at 128 deliberately. Batch size times fsync rate looks like it should be the ingest
    /// ceiling, and the batch does run saturated at ~127 of 128 records - but raising it to 1024
    /// measured slightly slower (4.931 against 5.104 msg/s on 2026-08-29), because a wider batch
    /// only waits longer to fill. The ceiling is upstream of the disk; see benchmarks.md.
    /// </remarks>
    public int FsyncBatchSize { get; set; } = 128;

    /// <summary>Maximum wall time one accepted MQTT publish waits for an fsync batch.</summary>
    public TimeSpan FsyncInterval { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Maximum records one flush reads at once.</summary>
    public int FlushBatchSize { get; set; } = 1024;

    /// <summary>Maximum decoded payload bytes one flush reads at once.</summary>
    public int FlushBatchBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Sustained flush ceiling in messages per second. Zero disables the limit — the setting lab
    /// §5.C10.3 flips to measure whether ADR-029 is buying anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zero, meaning no ceiling.</b> The default used to be 12.000, chosen to sit above N1 so a
    /// backlog could still be caught up — a sensible-sounding number picked before there was any
    /// data. Lab #3 then measured the pipeline draining at <b>13.224 msg/s</b>, so the ceiling sat
    /// BELOW real capacity and became the only thing deciding how fast a recovery went. It cost 165
    /// seconds on a thirty-minute backlog (+24%) and prevented nothing: ingestion never returned a
    /// single 429 or 503 in either arm, and D3's outage run recorded <c>rate_limited = 0</c> as well.
    /// </para>
    /// <para>
    /// The protection that does work is the closed loop: ingestion says it is overloaded with 429 or
    /// 503 and a <c>Retry-After</c>, and the flusher slows to that floor. That answers a real signal.
    /// A static ceiling answers a guess, and a guess that must be re-made every time the hardware
    /// changes is a guard nobody can trust.
    /// </para>
    /// <para>
    /// Still a knob, and lab #3 is the A/B that uses it. If M13 finds that many gateways reconnecting
    /// at once overwhelm ingestion in aggregate — which one gateway cannot demonstrate — the number
    /// that goes here has to come from a measured aggregate capacity, not from another guess. See
    /// ADR-029.
    /// </para>
    /// </remarks>
    public int FlushMessagesPerSecond { get; set; }

    /// <summary>Messages an idle gateway may send in one burst before the sustained rate applies.</summary>
    public int FlushBurstMessages { get; set; } = 12_000;

    /// <summary>First wait after a failed POST. Doubles from there.</summary>
    public TimeSpan FlushRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling on any single retry wait, however long ingestion has been down.</summary>
    public TimeSpan FlushRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fraction by which each retry wait is randomly stretched.</summary>
    public double FlushRetryJitterFraction { get; set; } = 0.25;

    /// <summary>Capacity of the in-memory handoff into the fsync writer.</summary>
    public int PendingWriteCapacity { get; set; } = 8192;

    /// <summary>Validates that every physical bound can make progress.</summary>
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
