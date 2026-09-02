namespace Nvm.Ingestion.RawCurves;

/// <summary>Giữ đúng các byte mà một máy tạo ra, cùng một chỉ mục có thể audit được.</summary>
/// <remarks>
/// Một interface để file-drop adapter phụ thuộc vào hành động archive thay vì phụ thuộc vào S3.
/// Việc của adapter là nhận ra một file là bản ghi của nhà máy và nói ai đã giao nó; các byte đó nằm
/// ở đâu, dưới object lock nào, là một quyết định mà adapter không nên có khả năng làm yếu đi một
/// cách vô tình.
/// </remarks>
public interface IRawCurveArchive
{
    /// <summary>Archive một stream chính xác một cách idempotent và trả về phiên bản object bất biến của nó.</summary>
    /// <param name="descriptor">Nhà máy, máy và interval mà các byte này thuộc về.</param>
    /// <param name="source">Các byte gốc, seekable và đang ở vị trí bắt đầu.</param>
    /// <param name="provenance">Ai đang archive, vì sao, và cái này sửa lại gì (K5).</param>
    /// <param name="cancellationToken">Dừng công việc khi host tắt.</param>
    Task<RawCurveArchiveResult> ArchiveAsync(
        RawCurveDescriptor descriptor,
        Stream source,
        RawCurveProvenance provenance,
        CancellationToken cancellationToken);
}
