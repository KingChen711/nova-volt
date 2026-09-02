namespace Nvm.Contracts.Events;

/// <summary>
/// Một sự thật đã xảy ra trên shop floor và đã được hệ thống chấp nhận là đúng.
/// </summary>
/// <remarks>
/// <para>
/// Domain event được đặt tên ở thì quá khứ và bằng ngôn ngữ của plant, không phải ngôn ngữ của code:
/// <c>ProductionUnitSerialized</c>, <c>UnitQuarantined</c>, <c>FormationRunCompleted</c>. Danh mục
/// đầy đủ nằm ở docs/scope.md §6.5.
/// </para>
/// <para>
/// Domain event không phải cùng một thứ với telemetry. Nếu nó thay đổi trạng thái nghiệp vụ của một
/// production unit thì đó là một event và thuộc về event store; nếu nó là quan sát liên tục — nhiệt
/// độ dryer mỗi 100 ms — thì đó là telemetry và thuộc về TimescaleDB. Ranh giới nằm ở
/// docs/scope.md §5.5, và làm sai ranh giới đó là cách event store biến thành một time-series
/// database mà không ai replay lại được.
/// </para>
/// <para>
/// Các implementation là record. Chúng không mang behaviour, không tham chiếu tới entity, và không
/// mang gì mà không sống sót được qua một vòng JSON: một event đọc lại vào năm 2036 chỉ có các field
/// của chính nó để làm việc.
/// </para>
/// <para>
/// Ở đây cố tình không có AggregateId. Aggregate sẽ đến ở M5, và một field mà chưa ai điền đúng được
/// là một field sẽ bị điền một cách cẩu thả. Nó sẽ được thêm bởi tầng sở hữu aggregate, không phải bởi
/// marker này.
/// </para>
/// </remarks>
public interface IDomainEvent
{
    /// <summary>
    /// Định danh của đúng một lần xảy ra này, dùng để nhận ra nó khi nó đến hai lần.
    /// </summary>
    /// <remarks>
    /// Bus là at-least-once, nên cùng một event sẽ được giao nhiều hơn một lần và mọi handler phải xử
    /// lý được điều đó (AGENTS.md K7). Giá trị này trở thành attribute <c>id</c> của CloudEvents trên
    /// wire, và với một event do một command sinh ra, nó phải bằng idempotency key của command đó —
    /// UUIDv5 tất định được dựng từ natural key trong docs/scope.md §7.2. Khi hai giá trị này trôi
    /// lệch nhau, việc khử trùng lặp ở ingestion và việc khử trùng lặp ở command handler bắt đầu key
    /// theo hai giá trị khác nhau và không cái nào còn hoạt động đúng.
    /// </remarks>
    Guid EventId { get; }

    /// <summary>
    /// Khi hệ thống ghi nhận sự thật này, luôn kèm UTC offset tường minh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Đây là <c>recorded_at</c> trong từ vựng của docs/scope.md §7.3 — đáng tin cậy nhất trong ba
    /// đồng hồ, và là cái mà audit và retention được đo theo.
    /// </para>
    /// <para>
    /// Đồng hồ của thiết bị không thuộc về đây. Một PLC có thể lệch hàng giờ và một gateway timestamp
    /// chỉ tốt bằng NTP của nó; cả hai được mang như các field bình thường bên trong những event có
    /// chúng, để chúng có thể bất đồng một cách công khai thay vì âm thầm ghi đè lên nhau.
    /// </para>
    /// <para>
    /// Type là <see cref="DateTimeOffset"/> chứ không bao giờ là <see cref="DateTime"/> (AGENTS.md
    /// K2): site DE1 áp dụng daylight saving time, nên một wall-clock reading không có offset sẽ mập
    /// mờ trong một giờ mỗi mùa thu. Analyzer NVM002 ép buộc điều này ngay lúc build.
    /// </para>
    /// </remarks>
    DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Plant mà sự thật này thuộc về, ví dụ <c>NV1</c> hoặc <c>DE1</c>.
    /// </summary>
    /// <remarks>
    /// Có mặt trên mọi event không ngoại lệ (AGENTS.md K3). Đây cũng là segment đầu tiên của routing
    /// key, <c>nvm.{site}.{context}.{event}.v{n}</c>, chính là cái cho phép một service subscribe vào
    /// đúng một plant. Authorization lọc theo nó ở phía server; một client tự khai báo site của mình
    /// không phải là một control, và một rò rỉ chéo site là một lỗi bảo mật chứ không phải một lỗi
    /// hiển thị (docs/scope.md §5.6).
    /// </remarks>
    string SiteId { get; }
}
