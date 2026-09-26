namespace Nvm.Contracts.Events.Equipment;

/// <summary>Máy đổi trạng thái chạy/dừng. Khi dừng phải có lý do là lá của cây lý do; stream <c>equipment:{path}</c>.</summary>
[EventContract("equipment", "equipment-state-changed")]
[EventVersion(1)]
public sealed record EquipmentStateChanged(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string EquipmentPath, string? PreviousState, string State, string? ReasonCode, string ActorId) : IDomainEvent;

/// <summary>
/// Một lần dừng đã kết thúc, đã phân loại theo scope §6.10: <c>Planned</c> (loại khỏi mẫu số Availability),
/// <c>Unplanned</c> (từ 5 phút, trừ vào Availability) hoặc <c>MicroStop</c> (dưới 5 phút, chỉ làm giảm Performance).
/// </summary>
[EventContract("equipment", "equipment-downtime-recorded")]
[EventVersion(1)]
public sealed record EquipmentDowntimeRecorded(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string EquipmentPath, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string ReasonCode,
    string Category, string ActorId) : IDomainEvent;

/// <summary>
/// Sản lượng của máy trong một khoảng [WindowFrom, WindowTo). Ideal cycle time được chốt lúc ghi (kèm version)
/// để OEE tính lại sau này không đổi khi master data đổi.
/// </summary>
[EventContract("equipment", "production-count-recorded")]
[EventVersion(1)]
public sealed record ProductionCountRecorded(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string EquipmentPath, string ProductCode, DateTimeOffset WindowFrom, DateTimeOffset WindowTo,
    long TotalCount, long GoodCount, int IdealCycleMilliseconds, int IdealCycleVersion, string ActorId) : IDomainEvent;
