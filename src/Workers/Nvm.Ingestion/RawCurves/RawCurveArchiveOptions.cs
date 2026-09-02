namespace Nvm.Ingestion.RawCurves;

/// <summary>Nơi giữ các byte gốc của một export từ máy.</summary>
/// <remarks>
/// Tắt theo mặc định vì archive thuộc về file-drop ingestion. Program khởi động sẽ thất bại khi file
/// drop được bật mà không có nó; ingestion chỉ dùng MQTT vẫn chạy được mà không cần object store vì
/// nó không bao giờ tiêu thụ một file export từ máy mà byte gốc của nó phải được giữ lại.
/// </remarks>
public sealed class RawCurveArchiveOptions
{
    /// <summary>Có archive bản gốc hay không.</summary>
    public bool Enabled { get; set; }

    /// <summary>Endpoint S3. MinIO bên trong compose network, một S3 region endpoint ở nơi khác.</summary>
    public string ServiceUrl { get; set; } = "http://minio:9000";

    /// <summary>Access key. Cấp qua environment, không bao giờ check-in (K13).</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret key. Cấp qua environment, không bao giờ check-in (K13).</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Bucket bị object-locked. Phải đã mang retention COMPLIANCE.</summary>
    public string BucketName { get; set; } = RawCurveArchiveStore.DefaultBucketName;

    /// <summary>Từ chối một cấu hình sẽ fail ở file đầu tiên thay vì fail ngay lúc khởi động.</summary>
    /// <exception cref="InvalidOperationException">Được bật mà thiếu endpoint hoặc credentials.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ServiceUrl)
            || string.IsNullOrWhiteSpace(AccessKey)
            || string.IsNullOrWhiteSpace(SecretKey)
            || string.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException(
                "NVM_INGEST:RawCurveArchive is enabled but is missing an endpoint, a bucket or "
                + "credentials. Failing here is deliberate: the alternative is a service that accepts "
                + "files for hours and only discovers it cannot keep their originals when someone "
                + "asks for one.");
        }
    }
}
