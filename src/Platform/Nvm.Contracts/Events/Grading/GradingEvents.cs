using System.Collections.Immutable;

namespace Nvm.Contracts.Events.Grading;

/// <summary>Một bin: khoảng nửa mở [min, max) của capacity (Ah), OCV (mV), DCIR (mΩ); Priority nhỏ gán trước.</summary>
public sealed record GradingBin(string BinCode, decimal CapacityMinAh, decimal CapacityMaxAh, decimal OcvMinMillivolt,
    decimal OcvMaxMillivolt, decimal DcirMinMilliOhm, decimal DcirMaxMilliOhm, int Priority);

/// <summary>Tiêu chí loại: <c>Metric</c> ∈ {CapacityAh, OcvMillivolt, DcirMilliOhm, OcvDriftMillivolt}, loại khi giá trị &gt; Threshold (hoặc &lt; nếu <c>Below</c>).</summary>
public sealed record GradingReject(string RejectCode, string Metric, decimal Threshold, bool Below);

/// <summary>Rule set được duyệt: từ <c>EffectiveFrom</c>, cell của sản phẩm được grade theo version này.</summary>
[EventContract("grading", "grading-rule-set-approved")]
[EventVersion(1)]
public sealed record GradingRuleSetApproved(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string RuleSetId, int Version, string ProductCode, DateTimeOffset EffectiveFrom,
    ImmutableArray<GradingBin> Bins, ImmutableArray<GradingReject> Rejects, string ContentSha256,
    string AuthoredBy, string ApprovedBy) : IDomainEvent;

/// <summary>
/// Một lần đánh giá cell theo một rule set. Không ghi đè lần trước: đánh giá lại tạo event mới cùng
/// <c>MeasurementId</c> và trỏ <c>SupersedesEvaluationId</c> về lần trước. Có đúng một trong BinCode/RejectCode.
/// </summary>
[EventContract("grading", "unit-graded")]
[EventVersion(1)]
public sealed record UnitGraded(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string SerialNumber, string ProductCode, string RuleSetId, int RuleSetVersion,
    string? BinCode, string? RejectCode, decimal CapacityAh, decimal OcvMillivolt, decimal DcirMilliOhm,
    decimal? OcvDriftMillivolt, Guid MeasurementId, Guid? SupersedesEvaluationId, string ActorId) : IDomainEvent;
