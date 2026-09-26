using System.Collections.Immutable;

namespace Nvm.Contracts.Events.Recipe;

/// <summary>Một tham số công nghệ: giá trị đặt và khoảng cho phép [Min, Max].</summary>
public sealed record RecipeParameter(string Name, decimal Target, decimal Min, decimal Max, string UnitOfMeasure);

/// <summary>
/// Recipe version được duyệt (kèm chữ ký điện tử) và có hiệu lực từ <c>EffectiveFrom</c> cho bộ khoá
/// (EquipmentClass, ProductCode, StepCode). Version active trước đó của cùng khoá kết thúc tại thời điểm này.
/// </summary>
[EventContract("recipe", "recipe-version-approved")]
[EventVersion(1)]
public sealed record RecipeVersionApproved(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string RecipeId, int Version, string ProductCode, string StepCode, string EquipmentClass,
    ImmutableArray<RecipeParameter> Parameters, DateTimeOffset EffectiveFrom, string ContentSha256,
    ImmutableArray<string> SignatureIds, int? SupersededVersion, string ApprovedBy) : IDomainEvent;

/// <summary>
/// Ghi recipe version nào đã dùng cho một lần chạy (lot/operation run) trên một máy. Lưu cả version và hash để
/// mười năm sau vẫn chứng minh được nội dung không đổi (scope §6.8).
/// </summary>
[EventContract("recipe", "recipe-version-applied")]
[EventVersion(1)]
public sealed record RecipeVersionApplied(
    Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string SiteId, string EquipmentPath, string EquipmentClass, string ProductCode, string StepCode,
    string RecipeId, int Version, string ContentSha256, string OperationRunId, string? LotId, string ActorId) : IDomainEvent;
