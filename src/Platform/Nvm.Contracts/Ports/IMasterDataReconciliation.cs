namespace Nvm.Contracts.Ports;

/// <summary>Loại định danh ERP cần ánh xạ về mã chuẩn của MES.</summary>
public static class IdentityKinds
{
    public const string Material = "Material";
    public const string Product = "Product";
    public const string Equipment = "Equipment";
}

/// <summary>Loại sai lệch master data. Mỗi loại mở một reconciliation task, không tự sửa.</summary>
public static class ReconciliationIssueKinds
{
    public const string UnknownMaterial = "UnknownMaterial";
    public const string UnknownProduct = "UnknownProduct";
    public const string UomMismatch = "UomMismatch";
}

/// <summary>Một sai lệch phát hiện khi nhận dữ liệu ERP, kèm chứng cứ để người xử lý đối chiếu.</summary>
public sealed record ReconciliationIssue(string Kind, string ExternalCode, string? CanonicalId, string? ReceivedUom,
    string? ExpectedUom, string SourceDocument, string Detail);

/// <summary>Mã ERP đã được giải: mã chuẩn và đơn vị gốc (với vật liệu).</summary>
public sealed record ResolvedIdentity(string CanonicalId, string? BaseUom);

/// <summary>
/// Master data của MES cho FB khác (work order, backflush) mà không phụ thuộc FB MasterData.
/// Mọi lời gọi chạy trong transaction của command hiện hành.
/// </summary>
public interface IMasterDataReconciliation
{
    /// <summary>Giải một mã ERP (đã chuẩn hoá khoảng trắng/chữ hoa) về mã chuẩn; null nếu chưa ai ánh xạ.</summary>
    Task<ResolvedIdentity?> ResolveAsync(string siteId, string kind, string externalCode, CancellationToken cancellationToken);

    /// <summary>true nếu sai lệch này đã được người có quyền chấp nhận cho đúng chứng từ này.</summary>
    Task<bool> IsAcceptedAsync(string siteId, ReconciliationIssue issue, CancellationToken cancellationToken);

    /// <summary>Mở task nếu chưa có task mở cho cùng sai lệch và chứng từ. Trả id của task.</summary>
    Task<string> OpenTaskAsync(string siteId, ReconciliationIssue issue, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Số tăng mỗi lần master data đổi; dùng làm khoá để đánh giá lại lệnh đang chờ.</summary>
    Task<long> RevisionAsync(string siteId, CancellationToken cancellationToken);
}
