namespace Nvm.Sparkplug;

/// <summary>Phần thân protobuf gateway-to-ingestion bị lỗi định dạng hoặc mâu thuẫn nội bộ.</summary>
public sealed class SparkplugIngressBatchException : Exception
{
    /// <summary>Tạo lỗi contract với một lý do an toàn để ghi vào log message bị từ chối.</summary>
    public SparkplugIngressBatchException(string message)
        : base(message)
    {
    }

    /// <summary>Bọc lỗi từ protobuf parser trong khi giữ nó ngoài public API.</summary>
    public SparkplugIngressBatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
