using System.ComponentModel.DataAnnotations;

namespace Nvm.PublicObjectModel;

/// <summary>
/// Một dòng WIP board: số unit nhóm theo (site, line, step, quality state) trong snapshot fixture.
/// </summary>
/// <remarks>
/// <para>
/// Đây là bảng snapshot đã gộp sẵn trong PostgreSQL, không phải projection tự cập nhật và không phải
/// một câu GROUP BY chạy trên toàn bảng lúc request. Gửi một kết quả đo không làm unit chuyển bước nên
/// số đếm không đổi giữa các lần đọc (ADR-038); M5/M6 mới thay bằng projection thật.
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
    [Key, MaxLength(48)]
    public required string Id { get; init; }

    [MaxLength(3)]
    public required string SiteId { get; init; }

    [MaxLength(2)]
    public required string Line { get; init; }

    [MaxLength(8)]
    public required string StepCode { get; init; }

    [MaxLength(16)]
    public required string QualityState { get; init; }

    public required int UnitCount { get; init; }

    [ConcurrencyCheck]
    public int Revision { get; init; }
}
