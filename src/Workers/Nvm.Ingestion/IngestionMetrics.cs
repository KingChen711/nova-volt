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

    private long _insertedCount;
    private long _duplicateCount;
    private long _driftedCount;

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
public sealed record IngestionResult(int Inserted, int Duplicates, int Drifted = 0);
