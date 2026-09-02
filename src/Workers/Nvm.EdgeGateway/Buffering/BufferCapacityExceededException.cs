namespace Nvm.EdgeGateway.Buffering;

/// <summary>Hàng đợi bền của edge đã chạm giới hạn đĩa đã cấu hình.</summary>
public sealed class BufferCapacityExceededException : IOException
{
    /// <summary>Tạo kết quả dừng cứng, báo cả số byte đo được lẫn số byte đã cấu hình.</summary>
    public BufferCapacityExceededException(long bytesOnDisk, long bytesRequested, long maxBytes)
        : base(
            $"Store-and-forward holds {bytesOnDisk} bytes and cannot append {bytesRequested}: "
            + $"the configured limit is {maxBytes}. New MQTT deliveries must stop; old records are never overwritten.")
    {
        BytesOnDisk = bytesOnDisk;
        BytesRequested = bytesRequested;
        MaxBytes = maxBytes;
    }

    /// <summary>Số byte của data-file có sẵn trước khi bị từ chối append.</summary>
    public long BytesOnDisk { get; }

    /// <summary>Số byte đã được framed mà caller cố thêm vào.</summary>
    public long BytesRequested { get; }

    /// <summary>Giới hạn cứng đã cấu hình.</summary>
    public long MaxBytes { get; }
}
