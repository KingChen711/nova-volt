namespace Nvm.Contracts.Events.Traceability;

/// <summary>
/// Bút toán bù trừ: unit đã được ghi nhầm vào <c>WrongParentSerialNumber</c>, thực ra nằm trong
/// <c>CorrectParentSerialNumber</c>. Cạnh sai được thay thế (CORRECTION), không bị xoá (K4/K5).
/// </summary>
[EventContract("traceability", "genealogy-correction-recorded")]
[EventVersion(1)]
public sealed record GenealogyCorrectionRecorded(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string ChildSerialNumber, string WrongParentSerialNumber,
    string CorrectParentSerialNumber, string Position, string ReasonText, string ActorId) : IDomainEvent;
