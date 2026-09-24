namespace Nvm.Contracts.Queries;

/// <summary>Trạng thái authoritative tối thiểu cho command của FB khác; không phải projection OData.</summary>
public sealed record UnitExecutionContext(string SiteId, string SerialNumber, string UnitKind,
    string OperationRunId, string StepCode, string EquipmentPath, string ExecutionState,
    string QualityState, string WorkOrderId);

/// <summary>Đọc unit trong site và transaction của command hiện tại.</summary>
/// <remarks>
/// Adapter phải giữ trạng thái đã đọc tới khi transaction kết thúc. Chỉ dùng trong host cùng
/// transaction SQL; không thay bằng một remote call hoặc read model eventually consistent.
/// </remarks>
public interface IUnitExecutionContextReader
{
    /// <summary>Null nếu unit chưa được serialize trong site đang xác thực.</summary>
    Task<UnitExecutionContext?> ReadForCommandAsync(string serialNumber, CancellationToken cancellationToken);
}
