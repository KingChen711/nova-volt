using System.Collections.Immutable;

namespace Nvm.Contracts.Events.Quality;

/// <summary>
/// Quality giữ một nguồn vật liệu (lot, cuộn hoặc đoạn cuộn [from, to)) hay một unit; mọi unit hạ nguồn phải bị giữ.
/// <c>TargetKind</c> ∈ {Lot, Roll, Unit}.
/// </summary>
[EventContract("quality", "quality-hold-placed")]
[EventVersion(1)]
public sealed record QualityHoldPlaced(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string HoldId, string TargetKind, string TargetId, decimal? SpanFromMeter, decimal? SpanToMeter,
    string ReasonCode, string? NcrId, string HeldBy) : IDomainEvent;

/// <summary>Job lan hold hạ nguồn bắt đầu: đã chốt danh sách unit bị ảnh hưởng.</summary>
[EventContract("quality", "hold-cascade-started")]
[EventVersion(1)]
public sealed record HoldCascadeStarted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string HoldId, string JobId, int TotalUnits, int ChunkSize) : IDomainEvent;

/// <summary>Một chunk của cascade đã giữ các unit này (thay cho một UnitQuarantined mỗi unit).</summary>
[EventContract("quality", "units-held-by-cascade")]
[EventVersion(1)]
public sealed record UnitsHeldByCascade(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string HoldId, string JobId, int ChunkIndex, ImmutableArray<string> SerialNumbers) : IDomainEvent;

/// <summary>Job cascade xong: mọi chunk đã commit.</summary>
[EventContract("quality", "hold-cascade-completed")]
[EventVersion(1)]
public sealed record HoldCascadeCompleted(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string HoldId, string JobId, int TotalUnits, int Chunks) : IDomainEvent;

/// <summary>Hold được thả sau khi đủ chữ ký điện tử của người khác người đã giữ.</summary>
[EventContract("quality", "quality-hold-released")]
[EventVersion(1)]
public sealed record QualityHoldReleased(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string HoldId, ImmutableArray<string> SignatureIds, string ReleasedBy) : IDomainEvent;

/// <summary>Chữ ký điện tử: ai, ý nghĩa gì, trên nội dung nào (hash), nối vào chuỗi hash của site.</summary>
[EventContract("quality", "electronic-signature-recorded")]
[EventVersion(1)]
public sealed record ElectronicSignatureRecorded(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SignatureId, string SubjectType, string SubjectId, string SignerId, string SignerRole,
    string Meaning, string ContentSha256, string PreviousHash, string Hash) : IDomainEvent;

/// <summary>MRB ra quyết định cho NCR: UseAsIs, Rework, Scrap hoặc Concession, kèm chữ ký.</summary>
[EventContract("quality", "disposition-applied")]
[EventVersion(1)]
public sealed record DispositionApplied(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string NcrId, string SerialNumber, string Disposition, ImmutableArray<string> SignatureIds,
    string DecidedBy) : IDomainEvent;

/// <summary>MRB loại bỏ unit; quyết định cuối, không quay lại.</summary>
[EventContract("quality", "unit-scrapped")]
[EventVersion(1)]
public sealed record UnitScrapped(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string ReasonCode, Guid CauseEventId, string ActorId) : IDomainEvent;

/// <summary>Unit được thả khỏi trạng thái giữ theo quyết định MRB. <c>NewState</c> là Released hoặc Rework.</summary>
[EventContract("quality", "unit-released-from-quarantine")]
[EventVersion(1)]
public sealed record UnitReleasedFromQuarantine(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string NewState, string ReasonCode, Guid CauseEventId, string ActorId) : IDomainEvent;
