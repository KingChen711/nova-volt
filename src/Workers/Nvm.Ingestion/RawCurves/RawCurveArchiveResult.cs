namespace Nvm.Ingestion.RawCurves;

/// <summary>The stable identity and physical S3 version of an archived curve.</summary>
/// <param name="ArchiveId">Deterministic identity of the plant/equipment/interval/digest tuple.</param>
/// <param name="ObjectKey">Content-addressed key inside the raw-curve bucket.</param>
/// <param name="ObjectVersionId">Exact immutable S3 version, not merely the current key.</param>
/// <param name="Sha256">Digest calculated before upload.</param>
/// <param name="ByteSize">Number of original bytes covered by the digest.</param>
/// <param name="ObjectCreated">Whether this call created the S3 object version.</param>
/// <param name="MetadataCreated">Whether this call appended the PostgreSQL metadata row.</param>
public sealed record RawCurveArchiveResult(
    Guid ArchiveId,
    string ObjectKey,
    string ObjectVersionId,
    string Sha256,
    long ByteSize,
    bool ObjectCreated,
    bool MetadataCreated);
