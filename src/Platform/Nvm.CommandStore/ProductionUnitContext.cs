using System.Data;
using System.Text.Json;

namespace Nvm.CommandStore;

/// <summary>Context kiểm điều kiện ghi; cùng nguồn fixture với POM, không đọc từ grid của client.</summary>
/// <param name="SiteId">Site sở hữu.</param>
/// <param name="SerialNumber">Serial bất biến.</param>
/// <param name="UnitKind">Cell/Module/Pack. Tên trường khớp <c>OperatorFixtureUnit.UnitKind</c> để một
/// tập fixture duy nhất round-trip qua JSON mà không mất trường (không dùng <c>Kind</c> — sẽ deserialize
/// ra null).</param>
/// <param name="OperationRunId">Lần chạy công đoạn.</param>
/// <param name="StepCode">Công đoạn được giao.</param>
/// <param name="EquipmentPath">Trạm được giao.</param>
/// <param name="ExecutionState">Trạng thái thực thi.</param>
/// <param name="QualityState">Trạng thái chất lượng.</param>
/// <param name="WorkOrderId">Lệnh sản xuất.</param>
public sealed record ProductionUnitContext(string SiteId, string SerialNumber, string UnitKind,
    string OperationRunId, string StepCode, string EquipmentPath, string ExecutionState,
    string QualityState, string WorkOrderId);

/// <summary>Đọc context trong chính transaction của command và ép site từ session.</summary>
public sealed class ProductionUnitContextReader(SqlCommandSession session)
{
    /// <summary>Không nhận SiteId từ caller; một serial ngoài site được xem như không tìm thấy.</summary>
    public async Task<ProductionUnitContext?> FindAsync(string serial, CancellationToken cancellationToken)
    {
        // Giữ read lock tới commit để state được kiểm không đổi giữa kiểm điều kiện và ghi effect,
        // kể cả khi principal quản trị cập nhật context. Runtime chỉ có quyền SELECT trên fixture.
        using var command = session.CreateCommand("""
            SELECT ContextJson FROM execution.UnitContext WITH (HOLDLOCK)
            WHERE SiteId = @site AND SerialNumber = @serial;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = session.SiteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serial;
        // Ép site TRƯỚC khi deserialize: một serial ngoài site không bao giờ chạm tới JSON.
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string json)
        {
            return null;
        }
        var context = JsonSerializer.Deserialize<ProductionUnitContext>(json);
        if (context is null || !string.Equals(context.SiteId, session.SiteId, StringComparison.Ordinal)
            || !string.Equals(context.SerialNumber, serial, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stored context identity does not match its key.");
        }
        return context;
    }
}
