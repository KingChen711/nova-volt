namespace Nvm.ProductionExecution.Commands;

/// <summary>Một quy tắc chặn cụ thể: cái gì cần đúng, và thực tế đang là gì.</summary>
/// <param name="Rule">Tên trường/điều kiện, để UI chỉ đúng chỗ.</param>
/// <param name="Expected">Giá trị cần có.</param>
/// <param name="Actual">Giá trị thực tế trong context của site.</param>
public sealed record BlockingRule(string Rule, string Expected, string Actual);

/// <summary>
/// Outcome của một lần nhập kết quả đo. Đây là TResult được lưu bền và replay y hệt cho bản gửi lại.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Accepted"/> = true nghĩa là kết quả đã commit, <b>không</b> nghĩa pack đã đạt chất lượng
/// (ADR-038). Business rejection cũng là một outcome hợp lệ (HTTP 200) và cũng được lưu để replay.
/// </para>
/// <para>
/// <see cref="CorrelationId"/> suy ra tất định từ idempotency key, nên replay trả về đúng cùng một
/// document — một correlation id sinh ngẫu nhiên mỗi request sẽ làm replay khác đi và phá vỡ D4/D5.
/// </para>
/// </remarks>
public sealed record RecordDataCollectionResult(
    bool Accepted,
    string ReasonCode,
    string ReasonText,
    IReadOnlyList<BlockingRule> BlockingRules,
    IReadOnlyList<string> AllowedNextActions,
    string CorrelationId);

/// <summary>Mã lý do ổn định cho outcome; UI map sang thông điệp, không đọc reasonText để rẽ nhánh.</summary>
public static class DataCollectionReasonCodes
{
    /// <summary>Đã ghi nhận kết quả đo.</summary>
    public const string Accepted = "ACCEPTED";

    /// <summary>Không tìm thấy serial trong site hiện tại (không lộ tồn tại ở site khác).</summary>
    public const string UnitNotFound = "UNIT_NOT_FOUND";

    /// <summary>Unit không phải pack.</summary>
    public const string NotAPack = "NOT_A_PACK";

    /// <summary>Operation run không thuộc pack này.</summary>
    public const string OperationRunMismatch = "OPERATION_RUN_MISMATCH";

    /// <summary>Công đoạn không phải EOL.</summary>
    public const string StepNotEol = "STEP_NOT_EOL";

    /// <summary>Trạm không khớp trạm được giao.</summary>
    public const string EquipmentMismatch = "EQUIPMENT_MISMATCH";

    /// <summary>Operation run chưa/không ở trạng thái Running.</summary>
    public const string OperationNotRunning = "OPERATION_NOT_RUNNING";

    /// <summary>Pack đang bị giữ chất lượng (Held).</summary>
    public const string QualityHold = "QUALITY_HOLD";

    /// <summary>Pack đã bị loại bỏ (Scrapped).</summary>
    public const string Scrapped = "SCRAPPED";
}

/// <summary>Hành động UI thực sự có màn hình xử lý ở M4. KHÔNG có Override/Release (ADR-038).</summary>
public static class DataCollectionNextActions
{
    /// <summary>Quay lại danh sách công việc.</summary>
    public const string ReturnToDispatch = "ReturnToDispatch";

    /// <summary>Chọn một unit khác để nhập.</summary>
    public const string SelectAnotherUnit = "SelectAnotherUnit";

    /// <summary>Bộ hành động tiêu chuẩn cho mọi outcome M4.</summary>
    public static IReadOnlyList<string> Standard { get; } = [ReturnToDispatch, SelectAnotherUnit];
}
