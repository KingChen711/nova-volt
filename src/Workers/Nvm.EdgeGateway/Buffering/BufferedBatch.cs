using System.Collections.Immutable;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Records read without moving the durable cursor.</summary>
/// <param name="Payloads">CRC-valid payloads; corrupt records are deliberately absent.</param>
/// <param name="Checkpoint">Where the cursor may move after the valid payloads are accepted.</param>
/// <param name="RecordsTraversed">Physical records crossed, including corrupt records skipped.</param>
public sealed record BufferedBatch(
    ImmutableArray<byte[]> Payloads,
    BufferCheckpoint Checkpoint,
    int RecordsTraversed)
{
    /// <summary>No data and no cursor progress.</summary>
    public static BufferedBatch Empty(BufferCheckpoint checkpoint) =>
        new(ImmutableArray<byte[]>.Empty, checkpoint, 0);

    /// <summary>Whether an acknowledgement would advance the cursor.</summary>
    public bool HasProgress => RecordsTraversed > 0;
}

/// <summary>A durable location between two framed records.</summary>
/// <param name="SegmentId">Monotonic segment number.</param>
/// <param name="Offset">Byte offset at a record boundary.</param>
/// <param name="AcknowledgedRecords">Total physical records passed since this queue was created.</param>
public sealed record BufferCheckpoint(long SegmentId, long Offset, long AcknowledgedRecords);
