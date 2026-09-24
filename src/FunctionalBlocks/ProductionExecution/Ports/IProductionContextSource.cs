namespace Nvm.ProductionExecution.Ports;

/// <summary>Context tối thiểu để quyết định có được nhập kết quả đo hay không, đã scope theo site.</summary>
/// <param name="SiteId">Site sở hữu.</param>
/// <param name="SerialNumber">Serial bất biến.</param>
/// <param name="UnitKind">Cell/Module/Pack.</param>
/// <param name="OperationRunId">Lần chạy công đoạn.</param>
/// <param name="StepCode">Công đoạn được giao.</param>
/// <param name="EquipmentPath">Trạm được giao.</param>
/// <param name="ExecutionState">Trạng thái thực thi (ví dụ Running).</param>
/// <param name="QualityState">Trạng thái chất lượng (ví dụ Pending/Held/Scrapped).</param>
/// <param name="WorkOrderId">Lệnh sản xuất.</param>
public sealed record ProductionUnitSnapshot(
    string SiteId,
    string SerialNumber,
    string UnitKind,
    string OperationRunId,
    string StepCode,
    string EquipmentPath,
    string ExecutionState,
    string QualityState,
    string WorkOrderId);

/// <summary>Đọc context của một unit trong site đã xác thực, bên trong transaction của command.</summary>
/// <remarks>
/// Là PORT: adapter SQL sống ở Nvm.ProductionExecution.Hosting và dùng CHUNG session/transaction với
/// claim + outcome. Site do session ép, nên một serial ngoài site trả về <c>null</c> — không lộ tồn tại.
/// </remarks>
public interface IProductionContextSource
{
    /// <summary>Tìm context theo serial trong site hiện tại; <c>null</c> nếu không có trong site.</summary>
    Task<ProductionUnitSnapshot?> FindAsync(string serial, CancellationToken cancellationToken);
}
