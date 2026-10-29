using Nvm.Foundation;

namespace Nvm.Project;

/// <summary>Một dòng của danh sách NC: NC của lớp Product nối thông tin của team và số MTU. Đọc từ view <c>dbo.NonConformanceListRow</c>.</summary>
/// <remarks>View cài bằng bước riêng (<c>make views</c>), không do Update Database tạo: thiếu view thì màn trống mà HTTP vẫn 200 (lab L15).</remarks>
[ReadingModel<NvOperationBlock>]
public sealed class NonConformanceListRow
{
    public string? NId { get; set; }

    public string? Status { get; set; }

    public string? Type { get; set; }

    public string? Severity { get; set; }

    public string? DefectNId { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    public DateTimeOffset? EndDate { get; set; }

    public string? Origin { get; set; }

    public string? Serial { get; set; }

    public string? OperationCode { get; set; }

    public string? StatusCode { get; set; }

    public string? SupplierNId { get; set; }

    public string? ScrapCode { get; set; }

    public string? ScrapTag { get; set; }

    public int MtuCount { get; set; }
}

public sealed class NonConformanceListParameters
{
    /// <summary>Rỗng: mọi trạng thái.</summary>
    public string? Status { get; init; }
}

/// <summary>Danh sách NC của màn chất lượng; OData lọc tiếp trên kết quả.</summary>
[ReadingFunction(NvProductionExtApp.Name)]
public sealed class NonConformanceList : IReadingFunction<NonConformanceListParameters, NonConformanceListRow>
{
    public IQueryable<NonConformanceListRow> Read(NonConformanceListParameters parameters, IReadingPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(platform);
        var rows = platform.ProjectionQuery<NonConformanceListRow>();
        return string.IsNullOrEmpty(parameters.Status) ? rows : rows.Where(r => r.Status == parameters.Status);
    }
}

/// <summary>
/// SQL của các view của team. <c>{{table:&lt;Lớp&gt;}}</c> là bảng của entity hoặc facet, <c>{{id:&lt;Facet&gt;}}</c> là cột Id của
/// entity trong bảng facet: tên bảng thật có hậu tố số, nên bước cài tra registry chứ không viết cứng.
/// </summary>
public static class NvViews
{
    public static IReadOnlyDictionary<string, string> Templates { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(NonConformanceListRow)] = """
            CREATE OR ALTER VIEW dbo.NonConformanceListRow AS
            SELECT nc.NId, nc.Status, nc.Type, nc.Severity, nc.DefectNId, nc.StartDate, nc.EndDate,
                   d.NonConformanceDetail_Origin AS Origin, d.NonConformanceDetail_Serial AS Serial,
                   d.NonConformanceDetail_OperationCode AS OperationCode, d.NonConformanceDetail_StatusCode AS StatusCode,
                   d.NonConformanceDetail_SupplierNId AS SupplierNId, d.NonConformanceDetail_ScrapCode AS ScrapCode,
                   d.NonConformanceDetail_ScrapTag AS ScrapTag,
                   (SELECT COUNT(*) FROM dbo.[{{table:NonConformanceItem}}] i
                    WHERE i.NonConformanceNId = nc.NId AND i.IsDeleted = 0) AS MtuCount
            FROM dbo.[{{table:NonConformance}}] nc
            LEFT JOIN dbo.[{{table:NonConformanceDetail}}] d
                   ON d.[{{id:NonConformanceDetail}}] = nc.Id AND d.IsDeleted = 0 AND d.FacetType = 'NonConformanceDetail'
            WHERE nc.IsDeleted = 0;
            """,
    };
}
