using System.Text.Json.Serialization;

namespace Nvm.Contracts.Events.ProductionExecution;

/// <summary>Một kết quả đo nhập tay đã được backend chấp nhận và ghi bền vững ở step EOL.</summary>
/// <param name="EventId">
/// Định danh lần xảy ra này, bằng đúng idempotency key của command đã sinh ra nó. Xem
/// <see cref="IDomainEvent.EventId"/>.
/// </param>
/// <param name="OccurredAt">
/// Thời điểm người vận hành xác nhận nhập trong Mendix — không phải timestamp thiết bị/gateway giả tạo
/// (ADR-038). Đây là <see cref="IDomainEvent.OccurredAt"/> và trở thành CloudEvents <c>time</c>.
/// </param>
/// <param name="RecordedAt">
/// Thời điểm server ghi nhận, lấy từ <see cref="TimeProvider"/> (AGENTS.md K1). Giữ tách khỏi
/// <see cref="OccurredAt"/> vì một đồng hồ do người dùng xác nhận và một đồng hồ server là hai sự thật
/// khác nhau; gộp lại thì downstream mất khả năng phân biệt độ trễ nhập liệu với độ trễ ghi nhận.
/// </param>
/// <param name="SiteId">Plant sở hữu kết quả này (K3).</param>
/// <param name="SubmissionId">Định danh draft ổn định do Mendix tạo một lần; giữ nguyên khi retry.</param>
/// <param name="SerialNumber">Serial pack đã đo.</param>
/// <param name="OperationRunId">Lần chạy công đoạn mà kết quả thuộc về.</param>
/// <param name="StepCode">Công đoạn — ở M4 là <c>EOL</c> (End-of-Line).</param>
/// <param name="EquipmentPath">Trạm được giao, dạng ISA-95 path đã resolve.</param>
/// <param name="SignalCode">Metric — ở M4 là <c>PackVoltage</c>.</param>
/// <param name="Value">
/// Giá trị đo, giữ dưới dạng <see cref="decimal"/> để không mất chữ số như <see cref="double"/>. Một
/// giá trị được ghi <b>không</b> đồng nghĩa pack đã đạt: M4 chưa định nghĩa ngưỡng chất lượng (ADR-038).
/// </param>
/// <param name="UnitOfMeasure">Đơn vị — ở M4 là <c>V</c>.</param>
/// <param name="ActorId">Người thực hiện, lấy từ principal đã xác thực (không do client tự khai).</param>
/// <remarks>
/// <para>
/// <b>Đây không phải <c>MeasurementRecorded</c> của luồng thiết bị, cũng không phải một event hoàn tất.</b>
/// ADR-038 chọn một contract riêng để không khẳng định "đã hoàn tất công đoạn" hay "đã đạt chất lượng"
/// khi chưa có logic quyết định điều đó. Tái dùng tên event cũ chỉ để giảm số class sẽ làm hồ sơ nói dối.
/// </para>
/// <para>
/// <b><see cref="EventId"/> bằng idempotency key của command.</b> Đây là điểm nối giữa dedup ở command
/// pipeline và <c>ce_id</c> trên wire; hai giá trị lệch nhau thì mỗi tầng key theo một thứ khác và không
/// tầng nào còn hoạt động đúng (docs/scope.md §7.2).
/// </para>
/// </remarks>
[EventContract("production-execution", "data-collection-recorded")]
[EventVersion(1)]
public sealed record DataCollectionRecorded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string SiteId,
    string SubmissionId,
    string SerialNumber,
    string OperationRunId,
    string StepCode,
    string EquipmentPath,
    string SignalCode,
    [property: JsonConverter(typeof(DataCollectionDecimalConverter))] decimal Value,
    string UnitOfMeasure,
    string ActorId) : IDomainEvent;
