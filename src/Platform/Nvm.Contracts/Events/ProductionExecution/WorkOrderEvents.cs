using System.Collections.Immutable;

namespace Nvm.Contracts.Events.ProductionExecution;

/// <summary>Một dòng vật liệu yêu cầu của work order: mã ERP gốc, mã chuẩn nếu đã giải, số lượng và đơn vị ERP gửi.</summary>
public sealed record WorkOrderMaterial(string ExternalMaterialId, string? MaterialId, decimal Quantity, string UnitOfMeasure);

/// <summary>
/// ERP gửi một work order (B2MML ProductionSchedule/ProductionRequest). Luôn được ghi, kể cả khi master data chưa khớp:
/// khi đó <c>Status</c> là <c>PendingMasterData</c> và <c>OpenTaskIds</c> chỉ ra việc cần xử lý. Không mất lệnh.
/// </summary>
[EventContract("production-execution", "work-order-received")]
[EventVersion(1)]
public sealed record WorkOrderReceived(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string WorkOrderId, string ScheduleId, string ExternalProductCode, string? ProductCode,
    DateTimeOffset? EarliestStart, ImmutableArray<WorkOrderMaterial> Materials, string Status,
    ImmutableArray<string> OpenTaskIds, string ActorId) : IDomainEvent;

/// <summary>Work order đủ master data và được phát xuống sản xuất.</summary>
[EventContract("production-execution", "work-order-released")]
[EventVersion(1)]
public sealed record WorkOrderReleased(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string WorkOrderId, string ProductCode, ImmutableArray<WorkOrderMaterial> Materials,
    long MasterDataRevision, string ActorId) : IDomainEvent;
