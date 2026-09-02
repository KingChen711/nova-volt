namespace Nvm.Contracts.Events;

/// <summary>
/// Khai báo schema version của một domain event type. Bắt buộc từ v1, trên mọi event.
/// </summary>
/// <remarks>
/// <para>
/// Version là một phần của contract, không phải metadata mô tả nó: nó kết thúc chuỗi event type
/// (<c>com.novavolt.traceability.unit-serialized.v1</c>) và routing key
/// (<c>nvm.NV1.traceability.unit-serialized.v1</c>), và được lưu cạnh mỗi dòng trong event store để
/// người đọc biết mình đang cầm hình dạng nào.
/// </para>
/// <para>Khi nào bump version, theo docs/scope.md §7.4:</para>
/// <list type="bullet">
///   <item><description>Thêm một field tùy chọn — không bump. Reader cũ sẽ bỏ qua nó.</description></item>
///   <item><description>
///     Đổi ý nghĩa một field, xóa nó, hoặc đổi type của nó — bump, và viết một upcaster từ version
///     trước. Golden file của version cũ không bao giờ bị sửa.
///   </description></item>
/// </list>
/// <para>
/// Áp dụng từ v1 thay vì "khi nào cần thì làm" chính là trọng tâm của quy tắc này (AGENTS.md K6). Đến
/// lúc cần một version thứ hai thì các event v1 đã nằm trong store và trên wire rồi, và không còn chỗ
/// nào để thêm cái marker lẽ ra chúng phải mang theo.
/// </para>
/// <para>
/// Attribute này không được kế thừa. Một event type dẫn xuất là một type khác với một wire name khác,
/// nên để nó lấy luôn số của type cha sẽ tạo ra hai payload khác nhau mà cả hai đều tự nhận là v1 —
/// và một chuỗi upcaster không thể phân biệt được chúng. Mỗi event type tự khai báo version của chính
/// nó, kể cả khi trông có vẻ thừa.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [EventVersion(1)]
/// public sealed record FactoryModelRevisionActivated(Guid EventId, DateTimeOffset OccurredAt, string SiteId)
///     : IDomainEvent;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventVersionAttribute(int version) : Attribute
{
    /// <summary>Schema version, bắt đầu từ 1.</summary>
    /// <remarks>
    /// Cố tình không validate gì ở đây. Một constructor của attribute mà ném lỗi chỉ fail khi có cái
    /// gì đó reflect qua type — tức là ở runtime, trong bất kỳ service nào chạm vào nó đầu tiên, rất
    /// lâu sau khi sai sót đã được commit. Thay vào đó, analyzer NVM003 từ chối một attribute bị thiếu
    /// và một version dưới 1 ngay lúc build, đúng là nơi một lỗi gõ nhầm trong hằng số nên bị bắt.
    /// </remarks>
    public int Version { get; } = version;
}
