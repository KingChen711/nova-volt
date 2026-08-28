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

    private long _insertedCount;
    private long _duplicateCount;

    /// <summary>Rows committed during this process lifetime.</summary>
    public long InsertedCount => Interlocked.Read(ref _insertedCount);

    /// <summary>Deliveries that resolved to a source id already present.</summary>
    public long DuplicateCount => Interlocked.Read(ref _duplicateCount);

    internal void RecordCommitted(IngestionResult result)
    {
        Interlocked.Add(ref _insertedCount, result.Inserted);
        Interlocked.Add(ref _duplicateCount, result.Duplicates);
        InsertedCounter.Add(result.Inserted);
        DuplicateCounter.Add(result.Duplicates);
    }
}

/// <summary>Outcome of one committed ingestion transaction.</summary>
/// <param name="Inserted">New logical readings stored.</param>
/// <param name="Duplicates">Repeated deliveries discarded.</param>
public sealed record IngestionResult(int Inserted, int Duplicates);
