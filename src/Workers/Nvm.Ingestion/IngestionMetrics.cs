using System.Diagnostics.Metrics;

namespace Nvm.Ingestion;

/// <summary>Process counters updated only after the database transaction commits.</summary>
public sealed class IngestionMetrics
{
    private static readonly Meter Meter = new("Nvm.Ingestion", "1.0.0");
    private static readonly Counter<long> InsertedCounter =
        Meter.CreateCounter<long>("nvm.ingest.inserted", unit: "{reading}");
    private static readonly Counter<long> DuplicateCounter =
        Meter.CreateCounter<long>("nvm.ingest.duplicates", unit: "{reading}");
    private static readonly Counter<long> DriftedCounter =
        Meter.CreateCounter<long>("nvm.ingest.drifted", unit: "{reading}");
    private static readonly Counter<long> PublishFailureCounter =
        Meter.CreateCounter<long>("nvm.ingest.publish_failures", unit: "{event}");
    private static readonly Counter<long> PublishedCounter =
        Meter.CreateCounter<long>("nvm.ingest.published", unit: "{event}");
    private static readonly Counter<long> WriteRetryCounter =
        Meter.CreateCounter<long>("nvm.ingest.write_retries", unit: "{transaction}");

    private long _insertedCount;
    private long _duplicateCount;
    private long _driftedCount;
    private long _publishFailureCount;
    private long _publishedCount;
    private long _writeRetryCount;

    /// <summary>Rows committed during this process lifetime.</summary>
    public long InsertedCount => Interlocked.Read(ref _insertedCount);

    /// <summary>Deliveries that resolved to a source id already present.</summary>
    public long DuplicateCount => Interlocked.Read(ref _duplicateCount);

    /// <summary>Rows stored whose device clock could not be trusted.</summary>
    /// <remarks>
    /// M3 computes production shifts from <c>device_timestamp</c>, so a run where this stays at zero
    /// is either a plant with perfect clocks or a fault that was never switched on. The second is far
    /// more likely, and this is the number that tells the two apart before M3 builds on the data.
    /// </remarks>
    public long DriftedCount => Interlocked.Read(ref _driftedCount);

    /// <summary>Events whose row is stored and whose announcement never reached the broker.</summary>
    /// <remarks>
    /// The measured cost of the dual write. ADR-022 put it at 18 of 200 when the broker dies
    /// mid-publish; M6's transactional outbox is what closes it. Until then this number is the honest
    /// statement of how much the bus is behind the database, and a run that reports zero has either
    /// been lucky or has not been watching.
    /// </remarks>
    public long PublishFailureCount => Interlocked.Read(ref _publishFailureCount);

    /// <summary>Events handed to the broker without an error.</summary>
    /// <remarks>
    /// The publisher-side half of the chaos lab. Without it, "how many were lost" can only be
    /// answered by counting rows and hoping every row produced an event — and the whitelist of
    /// scope.md §5.5 means most rows deliberately do not. A lab that has to assume its own input
    /// count measures nothing when the assumption breaks.
    /// </remarks>
    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    /// <summary>Write transactions PostgreSQL killed to break a deadlock, and this process retried.</summary>
    /// <remarks>
    /// <para>
    /// Non-zero is normal and near-constant per day. Creating a chunk takes a
    /// <c>ShareUpdateExclusiveLock</c> on the hypertable, so several writers reaching for the SAME
    /// not-yet-existing chunk deadlock, PostgreSQL kills all but one, and the losers retry into the
    /// chunk the winner made. Measured on 2.29.2-pg17: four writers, three killed on the first batch
    /// of a day and zero on every batch after it.
    /// </para>
    /// <para>
    /// It is counted rather than swallowed because the shape of the number is the diagnosis. A few per
    /// chunk boundary is the mechanism working; a number that climbs with load is contention of some
    /// other kind, and the two are indistinguishable if neither is counted.
    /// </para>
    /// </remarks>
    public long WriteRetryCount => Interlocked.Read(ref _writeRetryCount);

    internal void RecordWriteRetry()
    {
        Interlocked.Increment(ref _writeRetryCount);
        WriteRetryCounter.Add(1);
    }

    internal void RecordPublishOutcome(int attempted, int failures)
    {
        var published = attempted - failures;

        if (published > 0)
        {
            Interlocked.Add(ref _publishedCount, published);
            PublishedCounter.Add(published);
        }

        if (failures <= 0)
        {
            return;
        }

        Interlocked.Add(ref _publishFailureCount, failures);
        PublishFailureCounter.Add(failures);
    }

    internal void RecordCommitted(IngestionResult result)
    {
        Interlocked.Add(ref _insertedCount, result.Inserted);
        Interlocked.Add(ref _duplicateCount, result.Duplicates);
        Interlocked.Add(ref _driftedCount, result.Drifted);
        InsertedCounter.Add(result.Inserted);
        DuplicateCounter.Add(result.Duplicates);
        DriftedCounter.Add(result.Drifted);
    }
}

/// <summary>Outcome of one committed ingestion transaction.</summary>
/// <param name="Inserted">New logical readings stored.</param>
/// <param name="Duplicates">Repeated deliveries discarded.</param>
/// <param name="Drifted">Stored readings whose device clock disagreed with the gateway's.</param>
/// <param name="PublishFailures">
/// Events that could not be announced. The rows are stored regardless — that is the dual write
/// ADR-022 measured, kept visible rather than closed.
/// </param>
public sealed record IngestionResult(int Inserted, int Duplicates, int Drifted = 0, int PublishFailures = 0);
