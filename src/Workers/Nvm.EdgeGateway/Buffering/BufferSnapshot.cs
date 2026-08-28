namespace Nvm.EdgeGateway.Buffering;

/// <summary>A point-in-time view of the durable gateway queue.</summary>
/// <param name="Depth">Physical records not yet acknowledged, including a corrupt record waiting to be skipped.</param>
/// <param name="Bytes">Bytes occupied by data segments on disk.</param>
/// <param name="CorruptRecords">Records whose CRC failed during recovery.</param>
/// <param name="TruncatedTails">Incomplete final writes cut back to the last record boundary.</param>
/// <param name="MaxBytes">The hard disk cap.</param>
public sealed record BufferSnapshot(
    long Depth,
    long Bytes,
    long CorruptRecords,
    long TruncatedTails,
    long MaxBytes)
{
    /// <summary>Whether the next smallest framed record could still fit.</summary>
    public bool HasWriteRoom => Bytes + FileStoreAndForwardBuffer.RecordOverhead < MaxBytes;
}
