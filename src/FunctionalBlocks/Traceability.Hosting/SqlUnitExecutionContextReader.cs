using System.Data;
using Nvm.CommandStore;
using Nvm.Contracts.Queries;
using Nvm.Kernel.EventSourcing;
using Nvm.Traceability.Entities;

namespace Nvm.Traceability.Hosting;

/// <summary>Replay bằng domain của chủ unit; giữ lock SQL tới commit của command gọi qua Contracts.</summary>
public sealed class SqlUnitExecutionContextReader(SqlCommandSession session, IEventStore events)
    : IUnitExecutionContextReader
{
    /// <inheritdoc />
    public async Task<UnitExecutionContext?> ReadForCommandAsync(string serialNumber, CancellationToken cancellationToken)
    {
        // Cùng thứ tự với serialize/duplicate: reservation trước, stream sau. HOLDLOCK giữ cả
        // key chưa tồn tại, nên không rơi về fixture giữa lúc một unit thật đang được tạo.
        using var guard = session.CreateCommand("""
            SELECT QualityState FROM traceability.SerialReservations WITH (HOLDLOCK)
            WHERE SiteId = @site AND SerialNumber = @serial;
            """);
        guard.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = session.SiteId;
        guard.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
        var quality = await guard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;

        using var head = session.CreateCommand("""
            SELECT StreamType FROM es.Streams WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND StreamId = @serial;
            """);
        head.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = session.SiteId;
        head.Parameters.Add("@serial", SqlDbType.VarChar, 200).Value = serialNumber;
        var streamType = await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (quality is null && streamType is null)
        { return null; }
        if (quality is null || streamType != "production-unit")
        { throw new InvalidDataException("Unit reservation and event stream disagree."); }

        // Replay dùng cùng connection/transaction; không mở connection thứ hai có thể phải đợi
        // writer đang bị chính transaction này chặn.
        var stream = await events.ReadStreamAsync(session.SiteId, serialNumber, cancellationToken).ConfigureAwait(false);
        var unit = ProductionUnit.Replay(stream)
            ?? throw new InvalidDataException("Reserved production unit has no event stream.");
        return new UnitExecutionContext(unit.SiteId, unit.SerialNumber, unit.Kind.ToString(),
            unit.OperationRunId ?? "", unit.CurrentStep ?? "", unit.EquipmentPath ?? "",
            unit.Execution.ToString(), quality, unit.WorkOrderId);
    }
}
