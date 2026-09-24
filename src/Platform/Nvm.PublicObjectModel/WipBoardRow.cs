using System.ComponentModel.DataAnnotations;

namespace Nvm.PublicObjectModel;

/// <summary>
/// Một dòng WIP board: số unit nhóm theo (site, line, step, quality state).
/// </summary>
/// <remarks>
/// <para>
/// Query view đọc fixture trước khi cutover; sau cutover nó nhóm trạng thái sản phẩm từ projection.
/// Kết quả đo không tự chuyển bước; event start/complete và quality hold mới đổi nhóm tương ứng.
/// </para>
/// <para>
/// Nhóm theo <b>quality state</b> chứ không phải execution state vì màn hình WIP hỏi "bao nhiêu hàng
/// đang Pending / Held / Scrapped / Released ở mỗi bước". Fixture chưa có hàng đã giao; board giữ
/// riêng cả bucket Scrapped đang ở rack. Tổng này là số unit trong snapshot, không phải số hàng
/// được phép đi tiếp hoặc KPI WIP đang hoạt động.
/// </para>
/// </remarks>
public sealed class WipBoardRow
{
    // Preserve the imported external-key contract: Mendix cannot migrate its length in place.
    // Site (3) + line (2) + step (20) + quality (16) + separators (3) fit within 48.
    [Key, MaxLength(48)]
    public required string Id { get; init; }

    [MaxLength(3)]
    public required string SiteId { get; init; }

    [MaxLength(2)]
    public required string Line { get; init; }

    [MaxLength(20)]
    public required string StepCode { get; init; }

    [MaxLength(16)]
    public required string QualityState { get; init; }

    public required int UnitCount { get; init; }

    [ConcurrencyCheck]
    public int Revision { get; init; }
}
