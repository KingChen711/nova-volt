namespace Nvm.Contracts.Events.Quality;

/// <summary>
/// Mở một NCR (non-conformance report). <c>SourceEventId</c> là fact phát hiện sai lệch; <c>Source</c> là
/// FB phát hiện (ví dụ <c>formation-aging</c>, <c>operator</c>, <c>spc</c>).
/// </summary>
[EventContract("quality", "non-conformance-raised")]
[EventVersion(1)]
public sealed record NonConformanceRaised(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string NcrId, string SerialNumber, string ReasonCode, string Description,
    string Source, Guid SourceEventId, string ActorId) : IDomainEvent;
