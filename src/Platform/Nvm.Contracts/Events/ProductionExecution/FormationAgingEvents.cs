namespace Nvm.Contracts.Events.ProductionExecution;

/// <summary>Cell vào máy formation trên một khay, ở một kênh. Hạn hoàn tất formation đi kèm.</summary>
[EventContract("production-execution", "formation-run-started")]
[EventVersion(1)]
public sealed record FormationRunStarted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string TrayId, int Channel, string EquipmentPath,
    DateTimeOffset FormationDueAt, string ActorId) : IDomainEvent;

/// <summary>Formation xong: dung lượng đo được và (nếu có) URI đường cong thô đã lưu.</summary>
[EventContract("production-execution", "formation-run-completed")]
[EventVersion(1)]
public sealed record FormationRunCompleted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, decimal CapacityAh, string? CurveUri, string ActorId) : IDomainEvent;

/// <summary>Degas xong, đo OCV lần 1 và đặt cell vào kho aging: rack → level → channel.</summary>
[EventContract("production-execution", "aging-started")]
[EventVersion(1)]
public sealed record AgingStarted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, decimal Ocv1Millivolt, string TrayId, string RackId, int Level,
    int Channel, DateTimeOffset AgingDueAt, string ActorId) : IDomainEvent;

/// <summary>Đủ thời gian aging: saga timeout bắn, cell chờ đo OCV lần 2.</summary>
[EventContract("production-execution", "aging-period-elapsed")]
[EventVersion(1)]
public sealed record AgingPeriodElapsed(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, DateTimeOffset AgingDueAt) : IDomainEvent;

/// <summary>So OCV lần 2 với lần 1: drift vượt giới hạn là nghi tự phóng điện.</summary>
[EventContract("production-execution", "ocv-drift-evaluated")]
[EventVersion(1)]
public sealed record OcvDriftEvaluated(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, decimal Ocv1Millivolt, decimal Ocv2Millivolt, decimal DriftMillivolt,
    decimal LimitMillivolt, bool Passed, string ActorId) : IDomainEvent;

/// <summary>Quá trình formation/aging dừng ở trạng thái lỗi (ví dụ quá hạn formation).</summary>
[EventContract("production-execution", "formation-process-faulted")]
[EventVersion(1)]
public sealed record FormationProcessFaulted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string ReasonCode, string State) : IDomainEvent;
