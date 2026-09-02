namespace Nvm.Contracts.Events.FactoryModel;

/// <summary>
/// Một revision mới của factory model ISA-95 của một site đã trở thành revision đang có hiệu lực.
/// </summary>
/// <param name="EventId">Định danh của lần xảy ra này. Xem <see cref="IDomainEvent.EventId"/>.</param>
/// <param name="OccurredAt">Khi revision có hiệu lực. Xem <see cref="IDomainEvent.OccurredAt"/>.</param>
/// <param name="SiteId">Plant có model thay đổi, ví dụ <c>NV1</c>.</param>
/// <param name="Revision">Revision đang có hiệu lực. Luôn lớn hơn strictly so với revision trước.</param>
/// <param name="NodeCount">Cây có bao nhiêu node tại revision này.</param>
/// <param name="EquipmentPathsAdded">Các equipment path tồn tại ở revision này mà trước đó chưa có.</param>
/// <param name="EquipmentPathsRemoved">
/// Các equipment path từng tồn tại ở revision trước và nay không còn nữa.
/// </param>
/// <remarks>
/// <para>
/// Plant thì luôn thay đổi: một channel được thêm vào formation machine, một work cell được đưa đi
/// overhaul dài hạn, một line được đổi tên cho một sản phẩm mới. Mỗi cái đó là một revision, và không
/// cái nào chỉnh sửa lịch sử — model được đánh version vì cùng lý do event store là append-only.
/// </para>
/// <para>
/// Danh sách removal là nửa thú vị hơn. Một consumer đang giữ cây cached có thể áp dụng phần thêm vào
/// một cách vô điều kiện, nhưng một phần xóa lại là một quyết định: work in progress có thể vẫn đang
/// đứng trên cell vừa rời khỏi model, và các bản ghi traceability viết từ năm ngoái vẫn trỏ tới những
/// equipment path không còn resolve được nữa. Không cái nào trong hai trường hợp đó là lỗi dữ liệu, và
/// code coi một path bị thiếu là hỏng dữ liệu sẽ bắt đầu từ chối lịch sử hợp lệ ngay lần đầu tiên một
/// máy bị decommission.
/// </para>
/// </remarks>
[EventContract("factory-model", "revision-activated")]
[EventVersion(1)]
public sealed record FactoryModelRevisionActivated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string SiteId,
    int Revision,
    int NodeCount,
    IReadOnlyList<string> EquipmentPathsAdded,
    IReadOnlyList<string> EquipmentPathsRemoved) : IDomainEvent;
