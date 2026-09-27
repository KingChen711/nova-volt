using System.Collections.Immutable;

namespace Nvm.Contracts.Events.Passport;

/// <summary>Model pin (GTIN) dùng chung cho mọi pack cùng sản phẩm; quyết định sản phẩm có cần DPP hay không.</summary>
[EventContract("passport", "battery-model-defined")]
[EventVersion(1)]
public sealed record BatteryModelDefined(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string Gtin, string ProductCode, string Manufacturer, string Chemistry, decimal NominalEnergyKwh,
    int ExpectedLifetimeCycles, bool RequiresPassport, string ActorId) : IDomainEvent;

/// <summary>
/// Carbon footprint và tỉ lệ vật liệu tái chế theo (sản phẩm, nhà máy, năm). Ghi cho cả sản phẩm không cần passport.
/// </summary>
[EventContract("passport", "carbon-footprint-recorded")]
[EventVersion(1)]
public sealed record CarbonFootprintRecorded(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string ProductCode, int Year, int Version, decimal KgCo2ePerKwh,
    ImmutableDictionary<string, decimal> RecycledContentPercent, string VerifiedBy, string ActorId) : IDomainEvent;

/// <summary>Một version passport được compliance owner ký và công bố. Bất biến; cập nhật là version mới trỏ về version này.</summary>
[EventContract("passport", "passport-published")]
[EventVersion(1)]
public sealed record PassportPublished(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string Gtin, int Version, int? PreviousVersion, string ContentSha256,
    ImmutableArray<string> SignatureIds, string ActorId) : IDomainEvent;
