namespace Nvm.Contracts.Ports;

/// <summary>Kết quả mở NCR và giữ unit.</summary>
public sealed record QualityIncident(string NcrId, Guid NcrEventId);

/// <summary>
/// FB khác báo một sai lệch chất lượng cho Quality trong cùng transaction SQL của command đang chạy:
/// Quality giữ unit và mở NCR. FB gọi chỉ biết contract này, không biết FB Quality.
/// </summary>
public interface IQualityIncidents
{
    /// <summary>Idempotent theo <paramref name="sourceEventId"/>: gọi lại cùng fact trả cùng NCR.</summary>
    Task<QualityIncident> QuarantineWithNonConformanceAsync(string siteId, string serialNumber, string reasonCode,
        string description, string source, Guid sourceEventId, string actorId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}
