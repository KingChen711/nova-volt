using Nvm.CommandStore;
using Nvm.Contracts.Queries;
using Nvm.ProductionExecution.Ports;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Ưu tiên unit authoritative qua Contracts; giữ fixture cho submission PoC cũ chưa gửi.</summary>
/// <remarks>
/// Dùng chung <c>SqlCommandSession</c> scoped với claim/outcome (reader nhận session qua DI), nên đọc
/// context nằm trong CHÍNH transaction của command. Site do session ép; serial ngoài site trả null.
/// </remarks>
public sealed class SqlProductionContextSource(ProductionUnitContextReader reader,
    IUnitExecutionContextReader liveReader) : IProductionContextSource
{
    private readonly ProductionUnitContextReader _reader = reader;

    /// <inheritdoc />
    public async Task<ProductionUnitSnapshot?> FindAsync(string serial, CancellationToken cancellationToken)
    {
        if (await liveReader.ReadForCommandAsync(serial, cancellationToken)
                .ConfigureAwait(false) is { } live)
        {
            return new ProductionUnitSnapshot(live.SiteId, live.SerialNumber, live.UnitKind,
                live.OperationRunId, live.StepCode, live.EquipmentPath, live.ExecutionState,
                live.QualityState, live.WorkOrderId);
        }

        var context = await _reader.FindAsync(serial, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        return new ProductionUnitSnapshot(
            context.SiteId,
            context.SerialNumber,
            context.UnitKind,
            context.OperationRunId,
            context.StepCode,
            context.EquipmentPath,
            context.ExecutionState,
            context.QualityState,
            context.WorkOrderId);
    }
}
