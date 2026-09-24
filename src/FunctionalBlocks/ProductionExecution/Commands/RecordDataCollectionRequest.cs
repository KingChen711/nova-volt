using System.Text.Json.Serialization;

namespace Nvm.ProductionExecution.Commands;

/// <summary>Envelope request theo scope §7.5, hình dạng JSON phẳng để Mendix map dễ.</summary>
/// <param name="IdempotencyKey">
/// Khoá UUIDv5 tuỳ chọn. Backend luôn suy lại từ site đã xác thực và SubmissionId; nếu client gửi
/// khoá thì phải khớp. Client không có UUIDv5 vẫn retry ổn định với cùng SubmissionId.
/// </param>
/// <param name="SiteId">Site client khai; phải khớp principal, nếu không backend từ chối trước claim.</param>
/// <param name="OccurredAt">Thời điểm người dùng xác nhận nhập trong Mendix; giữ nguyên khi retry.</param>
/// <param name="Payload">Nội dung nghiệp vụ của lần nhập.</param>
public sealed record RecordDataCollectionRequest(
    string? IdempotencyKey,
    string SiteId,
    [property: JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonRequired] RecordDataCollectionPayload Payload);

/// <summary>Payload của một lần nhập kết quả đo. Ở M4 cố định: điện áp pack tại EOL.</summary>
/// <param name="SubmissionId">Định danh draft ổn định (UUID dạng D chữ thường), Mendix tạo một lần.</param>
/// <param name="Serial">Serial pack 16 ký tự.</param>
/// <param name="OperationRunId">Lần chạy công đoạn được nhập.</param>
/// <param name="StepCode">Công đoạn — M4 là <c>EOL</c>.</param>
/// <param name="EquipmentPath">Trạm được giao, dạng ISA-95 path.</param>
/// <param name="SignalCode">Metric — M4 là <c>PackVoltage</c>.</param>
/// <param name="Value">
/// Giá trị đo. <see cref="decimal"/> để giữ đủ chữ số; không suy ra ngưỡng đạt/không đạt (ADR-038).
/// </param>
/// <param name="UnitOfMeasure">Đơn vị — M4 là <c>V</c>.</param>
public sealed record RecordDataCollectionPayload(
    string SubmissionId,
    string Serial,
    string OperationRunId,
    string StepCode,
    string EquipmentPath,
    string SignalCode,
    [property: JsonRequired] decimal Value,
    string UnitOfMeasure);
