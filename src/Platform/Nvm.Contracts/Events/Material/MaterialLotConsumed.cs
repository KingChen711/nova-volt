namespace Nvm.Contracts.Events.Material;

/// <summary>
/// Một lot vật liệu (hoặc một đoạn của cuộn điện cực) đi vào một unit: cạnh TRANSFORMATION.
/// <c>SpanFromMeter</c>/<c>SpanToMeter</c> chỉ có với cuộn, là khoảng [from, to) trên cuộn.
/// </summary>
[EventContract("material", "material-lot-consumed")]
[EventVersion(1)]
public sealed record MaterialLotConsumed(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string LotId, string LotKind, string MaterialCode, string ConsumerSerialNumber,
    decimal Quantity, string UnitOfMeasure, decimal? SpanFromMeter, decimal? SpanToMeter,
    string OperationRunId, string ActorId) : IDomainEvent;

/// <summary>Nhận lot từ nhà cung cấp: số lượng, hạn dùng và giới hạn thời gian tiếp xúc sau khi mở bao.</summary>
[EventContract("material", "material-lot-received")]
[EventVersion(1)]
public sealed record MaterialLotReceived(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string LotId, string MaterialCode, decimal Quantity, string UnitOfMeasure, DateTimeOffset? ExpiresAt,
    int? MaxExposureMinutes, string? SupplierLotId, string ActorId) : IDomainEvent;

/// <summary>Lab đạt: lot được phép dùng.</summary>
[EventContract("material", "material-lot-released")]
[EventVersion(1)]
public sealed record MaterialLotReleased(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string LotId, string ActorId) : IDomainEvent;

/// <summary>Mở bao: đồng hồ tiếp xúc không khí bắt đầu chạy.</summary>
[EventContract("material", "material-lot-opened")]
[EventVersion(1)]
public sealed record MaterialLotOpened(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string LotId, string ActorId) : IDomainEvent;

/// <summary>Cho phép dùng lot vượt một luật (hết hạn, quá tiếp xúc, FIFO) tới <c>ValidUntil</c>, có chữ ký duyệt.</summary>
[EventContract("material", "material-override-granted")]
[EventVersion(1)]
public sealed record MaterialOverrideGranted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string OverrideId, string LotId, string Rule, string Justification,
    System.Collections.Immutable.ImmutableArray<string> SignatureIds, DateTimeOffset ValidUntil, string ActorId) : IDomainEvent;
