namespace Nvm.ProductionExecution.Ports;

/// <summary>Một bản ghi kết quả đo append-only, đủ để tái lập danh tính/ngữ cảnh/giá trị/actor/thời điểm.</summary>
/// <param name="SiteId">Site sở hữu (K3).</param>
/// <param name="IdempotencyKey">Khoá của command; cũng là PK cùng site, chống ghi hai lần.</param>
/// <param name="SubmissionId">Định danh draft ổn định.</param>
/// <param name="SerialNumber">Serial pack.</param>
/// <param name="OperationRunId">Lần chạy công đoạn.</param>
/// <param name="StepCode">Công đoạn (EOL).</param>
/// <param name="EquipmentPath">Trạm.</param>
/// <param name="SignalCode">Metric (PackVoltage).</param>
/// <param name="ValueText">
/// Giá trị đo dạng text round-trip của <see cref="decimal"/>. Cố ý là text để KHÔNG ép về fixed-scale
/// của SQL rồi mất chữ số — full decimal phải sống sót đúng nguyên vẹn.
/// </param>
/// <param name="UnitOfMeasure">Đơn vị (V).</param>
/// <param name="ActorId">Người thực hiện, từ principal.</param>
/// <param name="OccurredAt">Thời điểm người dùng xác nhận nhập.</param>
/// <param name="RecordedAt">Thời điểm server ghi nhận (TimeProvider).</param>
/// <param name="PayloadJson">Toàn bộ payload đã đóng băng, dạng JSON.</param>
public sealed record DataCollectionRecord(
    string SiteId,
    Guid IdempotencyKey,
    string SubmissionId,
    string SerialNumber,
    string OperationRunId,
    string StepCode,
    string EquipmentPath,
    string SignalCode,
    string ValueText,
    string UnitOfMeasure,
    string ActorId,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string PayloadJson);

/// <summary>Ghi (INSERT) một kết quả đo trong CHÍNH transaction của command.</summary>
/// <remarks>
/// Là PORT: adapter SQL dùng chung <c>SqlCommandSession</c> với claim/outcome, không mở connection thứ
/// hai. Bảng append-only: runtime chỉ SELECT/INSERT, DENY UPDATE/DELETE (migration 002).
/// </remarks>
public interface IDataCollectionStore
{
    /// <summary>Chèn bản ghi; ném nếu site không khớp session hoặc khoá đã tồn tại.</summary>
    Task AppendAsync(DataCollectionRecord record, CancellationToken cancellationToken);
}
