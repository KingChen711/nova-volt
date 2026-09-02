using System.Collections.Immutable;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Các record đọc được mà không làm dịch chuyển cursor bền (durable cursor).</summary>
/// <param name="Payloads">Các payload có CRC hợp lệ; record hỏng bị loại khỏi đây một cách cố ý.</param>
/// <param name="Checkpoint">Vị trí cursor có thể dịch tới sau khi các payload hợp lệ được chấp nhận.</param>
/// <param name="RecordsTraversed">Số record vật lý đã đi qua, kể cả các record hỏng bị bỏ qua.</param>
public sealed record BufferedBatch(
    ImmutableArray<byte[]> Payloads,
    BufferCheckpoint Checkpoint,
    int RecordsTraversed)
{
    /// <summary>Không có dữ liệu và cursor không tiến.</summary>
    public static BufferedBatch Empty(BufferCheckpoint checkpoint) =>
        new(ImmutableArray<byte[]>.Empty, checkpoint, 0);

    /// <summary>Việc acknowledge liệu có làm cursor tiến lên hay không.</summary>
    public bool HasProgress => RecordsTraversed > 0;
}

/// <summary>Một vị trí bền (durable) nằm giữa hai record đã được framed.</summary>
/// <param name="SegmentId">Số segment tăng đơn điệu (monotonic).</param>
/// <param name="Offset">Offset byte tại một ranh giới record.</param>
/// <param name="AcknowledgedRecords">Tổng số record vật lý đã đi qua kể từ khi hàng đợi này được tạo.</param>
public sealed record BufferCheckpoint(long SegmentId, long Offset, long AcknowledgedRecords);
