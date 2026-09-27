namespace Nvm.Contracts.Events.MasterData;

/// <summary>Một mã ERP được ánh xạ về mã chuẩn (thủ công hoặc seed), có người chịu trách nhiệm và lý do.</summary>
[EventContract("master-data", "identity-alias-mapped")]
[EventVersion(1)]
public sealed record IdentityAliasMapped(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string Kind, string ExternalCode, string CanonicalId, string Reason, long Revision,
    string ActorId) : IDomainEvent;

/// <summary>Một sai lệch master data cần người xử lý; mở từ luồng nhận dữ liệu ERP.</summary>
[EventContract("master-data", "reconciliation-task-opened")]
[EventVersion(1)]
public sealed record ReconciliationTaskOpened(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string TaskId, string IssueKind, string ExternalCode, string? CanonicalId, string? ReceivedUom,
    string? ExpectedUom, string SourceDocument, string Detail) : IDomainEvent;

/// <summary>Task được đóng: đã ánh xạ, hoặc sai lệch được chấp nhận cho đúng chứng từ đó (không quy đổi âm thầm).</summary>
[EventContract("master-data", "reconciliation-task-resolved")]
[EventVersion(1)]
public sealed record ReconciliationTaskResolved(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string TaskId, string Resolution, string Note, long Revision, string ActorId) : IDomainEvent;
