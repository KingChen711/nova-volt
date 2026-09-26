using Nvm.Quality.Entities;

namespace Nvm.Quality.Ports;

/// <summary>Một hold đang lưu. <c>ContentSha256</c> là thứ người ký phải ký khi thả hold.</summary>
public sealed record StoredHold(string HoldId, string TargetKind, string TargetId, decimal? SpanFromMeter,
    decimal? SpanToMeter, string ReasonCode, string? NcrId, string HeldBy, string Status, long StreamVersion)
{
    /// <summary>Nội dung chuẩn hoá được ký khi thả hold.</summary>
    public string ReleaseContent => SignatureChain.Content(string.Join('|', "HOLD-RELEASE", HoldId, TargetKind, TargetId,
        SpanFromMeter?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        SpanToMeter?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", ReasonCode, NcrId ?? ""));
}

/// <summary>Tiến độ job cascade: đã chốt danh sách chưa, bao nhiêu unit, đã xong tới chunk nào.</summary>
public sealed record CascadeProgress(string JobId, string HoldId, string Status, int TotalUnits, int NextChunk, int ChunkSize,
    long StreamVersion);

/// <summary>Hold, job cascade và thành viên hold, trong transaction của command.</summary>
public interface IHoldStore
{
    Task<StoredHold?> LoadForUpdateAsync(string siteId, string holdId, CancellationToken cancellationToken);

    Task CreateAsync(string siteId, StoredHold hold, DateTimeOffset at, CancellationToken cancellationToken);

    Task SetReleasedAsync(string siteId, string holdId, long streamVersion, DateTimeOffset at, CancellationToken cancellationToken);

    Task<CascadeProgress?> LoadJobForUpdateAsync(string siteId, string jobId, CancellationToken cancellationToken);

    /// <summary>Thêm unit mục tiêu chưa có (idempotent) và trả tổng số mục tiêu của job.</summary>
    Task<int> AddTargetsAsync(string siteId, string jobId, string holdId, IReadOnlyList<string> serials, int chunkSize,
        DateTimeOffset at, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ReadChunkAsync(string siteId, string jobId, int chunkIndex, int chunkSize,
        CancellationToken cancellationToken);

    /// <summary>Ghi thành viên hold cho các serial (bỏ qua serial đã là thành viên) và tiến checkpoint.</summary>
    Task ApplyChunkAsync(string siteId, string jobId, string holdId, int chunkIndex, int chunkSize,
        IReadOnlyList<string> serials, bool completed, long streamVersion, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Ghi version stream cascade sau event bắt đầu (không đọc lại stream mỗi chunk).</summary>
    Task SetStreamVersionAsync(string siteId, string jobId, long streamVersion, CancellationToken cancellationToken);
}

/// <summary>Chuỗi chữ ký điện tử của site; append khoá đầu chuỗi để hai chữ ký không cùng nối vào một hash.</summary>
public interface ISignatureStore
{
    Task<string> HeadForUpdateAsync(string siteId, CancellationToken cancellationToken);

    Task AppendAsync(string siteId, SignatureRecord signature, CancellationToken cancellationToken);

    Task<IReadOnlyList<SignatureRecord>> LoadAsync(string siteId, IReadOnlyCollection<string> signatureIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SignatureRecord>> ChainAsync(string siteId, CancellationToken cancellationToken);
}

/// <summary>NCR đã mở.</summary>
public sealed record StoredNcr(string NcrId, string SerialNumber, string ReasonCode, string Description, string RaisedBy,
    string Status, long StreamVersion)
{
    public string DispositionContent(string disposition) =>
        SignatureChain.Content(string.Join('|', "NCR-DISPOSITION", NcrId, SerialNumber, ReasonCode, disposition));
}

public interface INcrStore
{
    Task<StoredNcr?> LoadNcrForUpdateAsync(string siteId, string ncrId, CancellationToken cancellationToken);

    Task CloseAsync(string siteId, string ncrId, string disposition, long streamVersion, DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <summary>Đổi facet chất lượng của một unit theo quyết định MRB.</summary>
public interface IUnitQualityWriter
{
    Task SetStateAsync(string siteId, string serialNumber, QualityState state, string reasonCode, Guid causeEventId,
        string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}

/// <summary>Unit hạ nguồn của một nguồn vật liệu, đọc từ read model genealogy.</summary>
public interface IDownstreamUnits
{
    Task<IReadOnlyList<string>> ReadAsync(string siteId, string targetKind, string targetId, decimal? spanFromMeter,
        decimal? spanToMeter, CancellationToken cancellationToken);
}
