namespace Nvm.Simulator.Publishing;

/// <summary>Một message không thể được đặt lên đường truyền.</summary>
/// <remarks>
/// <para>
/// Đường truyền hỏng, không phải nhà máy hỏng. Sự phân biệt này chính là toàn bộ lý do type này tồn
/// tại: worker phải tiếp tục chạy khi broker biến mất (N15) và KHÔNG được tiếp tục chạy khi chính line
/// bị sai, và một <c>catch</c> trần trụi quanh publish sẽ khiến hai trường hợp đó không thể phân biệt
/// được.
/// </para>
/// <para>
/// Được khai báo ở đây thay vì để exception của chính MQTTnet đi xuyên qua, vì worker được viết dựa
/// trên <see cref="ISparkplugPublisher"/> và không có việc gì phải biết transport bên dưới là gì. Một
/// publisher nói một protocol khác vẫn sẽ báo cùng một lỗi theo đúng cùng một cách.
/// </para>
/// </remarks>
public sealed class SparkplugPublishException : Exception
{
    /// <summary>Tạo exception.</summary>
    public SparkplugPublishException()
    {
    }

    /// <summary>Tạo exception.</summary>
    /// <param name="message">Cái gì không gửi được, và nó định đi đâu.</param>
    public SparkplugPublishException(string message)
        : base(message)
    {
    }

    /// <summary>Tạo exception.</summary>
    /// <param name="message">Cái gì không gửi được, và nó định đi đâu.</param>
    /// <param name="innerException">Transport đã báo gì.</param>
    public SparkplugPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
