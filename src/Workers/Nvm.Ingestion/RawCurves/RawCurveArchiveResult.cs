namespace Nvm.Ingestion.RawCurves;

/// <summary>Định danh ổn định và phiên bản S3 vật lý của một curve đã archive.</summary>
/// <param name="ArchiveId">Định danh deterministic của bộ tuple plant/equipment/interval/digest.</param>
/// <param name="ObjectKey">Key content-addressed bên trong raw-curve bucket.</param>
/// <param name="ObjectVersionId">Phiên bản S3 bất biến chính xác, không chỉ là key hiện tại.</param>
/// <param name="Sha256">Digest tính trước khi upload.</param>
/// <param name="ByteSize">Số byte gốc mà digest bao phủ.</param>
/// <param name="ObjectCreated">Liệu lời gọi này có tạo ra phiên bản object S3 hay không.</param>
/// <param name="MetadataCreated">Liệu lời gọi này có thêm dòng metadata PostgreSQL hay không.</param>
public sealed record RawCurveArchiveResult(
    Guid ArchiveId,
    string ObjectKey,
    string ObjectVersionId,
    string Sha256,
    long ByteSize,
    bool ObjectCreated,
    bool MetadataCreated);
