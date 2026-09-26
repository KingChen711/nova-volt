namespace Nvm.Contracts.Events.Quality;

/// <summary>
/// Quality giữ một unit; unit không được xử lý tiếp cho tới khi có quyết định release.
/// <c>CauseEventId</c> là sự kiện gây ra việc giữ (sự cố serial trùng, hold cascade, NCR...).
/// </summary>
[EventContract("quality", "unit-quarantined")]
[EventVersion(1)]
public sealed record UnitQuarantined(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string ReasonCode, Guid CauseEventId,
    string ActorId) : IDomainEvent;
