namespace Nvm.EdgeGateway.Buffering;

/// <summary>Một góc nhìn tại-một-thời-điểm của hàng đợi bền (durable queue) của gateway.</summary>
/// <param name="Depth">Số record vật lý chưa được acknowledge, kể cả một record hỏng đang chờ bị bỏ qua.</param>
/// <param name="Bytes">Số byte chiếm bởi các data segment trên đĩa.</param>
/// <param name="CorruptRecords">Số record có CRC sai khi phục hồi (recovery).</param>
/// <param name="TruncatedTails">Số lần ghi cuối bị dở dang, đã bị cắt lùi về ranh giới record hoàn chỉnh gần nhất.</param>
/// <param name="DataFsyncs">
/// Số lần gọi fsync vật lý trên data-file kể từ khi process khởi động. Chia cho số record đi qua các
/// lần fsync đó cho biết đây là "một fsync mỗi message" hay "một fsync mỗi batch" mà không cần đoán.
/// </param>
/// <param name="MaxBytes">Trần dung lượng đĩa cứng.</param>
public sealed record BufferSnapshot(
    long Depth,
    long Bytes,
    long CorruptRecords,
    long TruncatedTails,
    long DataFsyncs,
    long MaxBytes)
{
    /// <summary>Record nhỏ nhất kế tiếp đã được framed liệu còn vừa hay không.</summary>
    public bool HasWriteRoom => Bytes + FileStoreAndForwardBuffer.RecordOverhead < MaxBytes;
}
