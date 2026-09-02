namespace Nvm.EdgeGateway;

/// <summary>Địa chỉ và thời gian cho đường dẫn OT-to-DMZ.</summary>
public sealed class EdgeGatewayOptions
{
    /// <summary>Tên service EMQX trong <c>dmz-net</c>.</summary>
    public string BrokerHost { get; set; } = "emqx";

    /// <summary>Cổng lắng nghe MQTT bên trong Docker network.</summary>
    public int BrokerPort { get; set; } = 1883;

    /// <summary>Định danh mà broker nhìn thấy.</summary>
    public string ClientId { get; set; } = "nvm-edge-gateway";

    /// <summary>Endpoint HTTP có versioning thuộc quyền sở hữu của ingestion.</summary>
    public string IngestionUrl { get; set; } = "http://ingestion:8080/api/ingestion/v1/sparkplug-batches";

    /// <summary>Thời gian tối đa một lần flush buffer được phép chờ ingestion.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Thời gian chờ trước khi kết nối lại với EMQX.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>EMQX giữ session MQTT 5 này bao lâu trong khi gateway bị ngắt kết nối.</summary>
    /// <remarks>
    /// MQTT 5 mặc định giá trị này bằng zero ngay cả khi Clean Start là false. Giá trị zero sẽ hủy
    /// mọi delivery QoS 1 vẫn đang chờ acknowledgement sau-fsync tại thời điểm ngắt kết nối.
    /// </remarks>
    public TimeSpan SessionExpiryInterval { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Thư mục chứa các document factory-model bất biến.</summary>
    public string SeedDirectory { get; set; } = "seed";

    /// <summary>Revision mà gateway này đọc; null nghĩa là chọn document được publish mới nhất.</summary>
    public int? Revision { get; set; }

    /// <summary>Bao nhiêu message đã decode trôi qua giữa các lần ghi log tiến độ.</summary>
    public int LogEvery { get; set; } = 1000;

    /// <summary>Snapshot vận hành nguyên tử (atomic) mà các lab fail-closed của M2 tiêu thụ.</summary>
    /// <remarks>
    /// Null đặt file bên cạnh các segment durable. Đây không phải telemetry hay một truy vấn
    /// nghiệp vụ; đây là một công cụ chẩn đoán trong một tiến trình duy nhất, cần thiết để so sánh
    /// các counter chính xác mà không phải cào (scrape) log đã lấy mẫu.
    /// </remarks>
    public string? DiagnosticsPath { get; set; }

    /// <summary>Cấu hình hàng đợi durable.</summary>
    public Buffering.PersistentBufferOptions Buffer { get; set; } = new();

    /// <summary>File diagnostics đã cấu hình hoặc được suy ra.</summary>
    public string ResolvedDiagnosticsPath => DiagnosticsPath ?? Path.Combine(Buffer.DirectoryPath, "gateway.stats");

    /// <summary>Endpoint đã được xác thực.</summary>
    public Uri IngestionEndpoint => new(IngestionUrl, UriKind.Absolute);

    /// <summary>Từ chối một cấu hình gateway không thể mang traffic.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SeedDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BrokerPort);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LogEvery);
        Buffer.Validate();

        if (DiagnosticsPath is not null && string.IsNullOrWhiteSpace(DiagnosticsPath))
        {
            throw new InvalidOperationException("A configured diagnostics path must not be blank.");
        }

        if (Revision is < 1)
        {
            throw new InvalidOperationException("A configured factory-model revision must be at least 1.");
        }

        if (RequestTimeout <= TimeSpan.Zero || ReconnectDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Request timeout and reconnect delay must both be positive.");
        }

        if (SessionExpiryInterval <= TimeSpan.Zero
            || SessionExpiryInterval.TotalSeconds > uint.MaxValue)
        {
            throw new InvalidOperationException(
                "MQTT session expiry must be positive and fit the protocol's uint32-second field.");
        }

        if (!Uri.TryCreate(IngestionUrl, UriKind.Absolute, out var endpoint)
            || (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"IngestionUrl '{IngestionUrl}' must be an absolute HTTP or HTTPS URL.");
        }
    }
}
