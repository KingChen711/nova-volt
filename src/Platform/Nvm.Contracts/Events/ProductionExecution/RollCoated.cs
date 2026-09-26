using System.Collections.Immutable;

namespace Nvm.Contracts.Events.ProductionExecution;

/// <summary>Một đoạn trên cuộn đã phủ: khoảng mét [from, to) của một mặt, cùng nguồn vật liệu.</summary>
public sealed record RollSegment(
    string WebSide, decimal FromMeter, decimal ToMeter, string SlurryBatchId, string FoilLotId,
    string RecipeVersionId, string EquipmentId);

/// <summary>Phủ xong một cuộn điện cực, kèm bản đồ đoạn để trace theo vị trí mét (scope §6.4).</summary>
[EventContract("production-execution", "roll-coated")]
[EventVersion(1)]
public sealed record RollCoated(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string RollId, ImmutableArray<RollSegment> Segments, string ActorId) : IDomainEvent;
