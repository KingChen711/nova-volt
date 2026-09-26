namespace Nvm.Contracts.Queries;

/// <summary>Trường QualityState mà FB Quality đóng góp cho ProductionUnit (facet).</summary>
/// <param name="QualityState">Giá trị do Quality sở hữu: Pending, Released, Held, Rework, Scrapped.</param>
/// <param name="BlockingReasonCode">Khác null khi Quality cấm xử lý tiếp unit; chủ unit trả nguyên mã này.</param>
public sealed record UnitQualityFacet(string QualityState, string? BlockingReasonCode)
{
    /// <summary>Unit chưa có quyết định chất lượng nào.</summary>
    public static UnitQualityFacet Pending { get; } = new("Pending", null);
}

/// <summary>Facet chất lượng của unit, đọc và ghi trong transaction SQL của command đang chạy.</summary>
/// <remarks>
/// Chủ unit (Traceability) chỉ biết interface này trong Contracts, không biết FB Quality tồn tại.
/// Host ghép adapter của Quality vào. Adapter phải giữ lock tới commit để guard không bị lệch.
/// </remarks>
public interface IUnitQualityFacet
{
    /// <summary>Quality state hiện tại; unit chưa có dòng nào được coi là Pending.</summary>
    Task<UnitQualityFacet> ReadForCommandAsync(string siteId, string serialNumber,
        CancellationToken cancellationToken);

    /// <summary>Giữ unit khi chủ unit phát hiện sự cố nhận dạng (serial trùng), cùng transaction với sự cố.</summary>
    Task QuarantineForIncidentAsync(string siteId, string serialNumber, Guid incidentEventId,
        string reasonCode, string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}
