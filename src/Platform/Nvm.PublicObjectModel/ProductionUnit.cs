using System.ComponentModel.DataAnnotations;

namespace Nvm.PublicObjectModel;

/// <summary>
/// Read DTO phẳng của một production unit cho Operator Station: đủ ngữ cảnh để chọn việc, quét serial
/// và nhìn ba loại state tách biệt. Query views được chuyển từ fixture sang projection khi deploy.
/// </summary>
/// <remarks>
/// Ba state là ba trục độc lập (glossary §7): execution thuộc thao tác, quality độc lập vị trí vật lý,
/// location là chỗ hàng đang nằm. Gộp chúng thành một enum thì một lần chuyển kho vô tình release hàng
/// đang bị giữ — đúng loại lỗi audit bắt được. Vì vậy chúng là ba cột riêng, không suy ra được của nhau.
/// </remarks>
public sealed class ProductionUnit
{
    [Key, MaxLength(20)]
    public required string Id { get; init; }

    [MaxLength(3)]
    public required string SiteId { get; init; }

    // Serial 16 ký tự đã qua Nvm.Kernel.Identity.SerialNumber ở generator; không nhận serial thiếu ký tự.
    [MaxLength(16)]
    public required string SerialNumber { get; init; }

    // Cell / Module / Pack — scan cần biết tier để hiển thị đúng và để backend kiểm "unit là pack" ở EOL.
    [MaxLength(8)]
    public required string UnitKind { get; init; }

    [MaxLength(2)]
    public required string Line { get; init; }

    [MaxLength(200)]
    public required string Resource { get; init; }

    [MaxLength(256)]
    public required string EquipmentPath { get; init; }

    [MaxLength(100)]
    public required string WorkOrderId { get; init; }

    [MaxLength(100)]
    public required string OperationRunId { get; init; }

    [MaxLength(20)]
    public required string StepCode { get; init; }

    // Execution state của operation run: Scheduled / Running / Completed / Aborted.
    [MaxLength(16)]
    public required string ExecutionState { get; init; }

    // Quality state của unit: Pending / Released / Held / Rework / Scrapped. Độc lập với vị trí.
    [MaxLength(16)]
    public required string QualityState { get; init; }

    // Vị trí không suy ra từ quality state; fixture chưa có hàng đã giao.
    [MaxLength(24)]
    public required string LocationState { get; init; }

    // Bao gồm quality hold/scrap và operation chưa Running; UI không tự suy ra quyền nhập từ vị trí.
    [MaxLength(32)]
    public string? BlockingReasonCode { get; init; }

    [MaxLength(256)]
    public string? BlockingReasonText { get; init; }

    [ConcurrencyCheck]
    public int Revision { get; init; }
}
