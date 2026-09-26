using Nvm.ProductionExecution.Entities;

namespace Nvm.ProductionExecution.Ports;

/// <summary>Một timeout đến hạn chờ được bắn.</summary>
public sealed record DueTimeout(string SiteId, string SerialNumber, string Kind, DateTimeOffset DueAt);

/// <summary>
/// Trạng thái quá trình và timeout bền, trong cùng transaction với event của command. Timeout nằm trong DB
/// chứ không trong RAM hay trong broker, nên restart service không làm mất hạn chờ nhiều ngày.
/// </summary>
public interface IFormationProcessStore
{
    Task<FormationAgingProcess?> LoadForUpdateAsync(string siteId, string serialNumber, CancellationToken cancellationToken);

    /// <summary>Ghi trạng thái; <c>process.Version</c> phải là version stream sau event vừa append.</summary>
    Task SaveAsync(FormationAgingProcess process, bool isNew, DateTimeOffset at, CancellationToken cancellationToken);

    Task ScheduleAsync(string siteId, string serialNumber, string kind, DateTimeOffset dueAt, CancellationToken cancellationToken);

    /// <summary>Đánh dấu timeout đã xử lý (bắn hoặc không còn áp dụng). Trả false nếu đã đánh dấu trước đó.</summary>
    Task<bool> CompleteTimeoutAsync(string siteId, string serialNumber, string kind, DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <summary>Worker đọc timeout đến hạn theo <see cref="TimeProvider"/>, ngoài transaction command.</summary>
public interface IDueTimeoutSource
{
    Task<IReadOnlyList<DueTimeout>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}
