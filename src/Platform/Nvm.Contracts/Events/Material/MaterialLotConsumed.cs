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
