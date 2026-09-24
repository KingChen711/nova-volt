using Nvm.CommandStore;
using Nvm.ProductionExecution.Ports;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Adapter đọc context: bọc <see cref="ProductionUnitContextReader"/> của CommandStore.</summary>
/// <remarks>
/// Dùng chung <c>SqlCommandSession</c> scoped với claim/outcome (reader nhận session qua DI), nên đọc
/// context nằm trong CHÍNH transaction của command. Site do session ép; serial ngoài site trả null.
/// </remarks>
public sealed class SqlProductionContextSource(ProductionUnitContextReader reader) : IProductionContextSource
{
    private readonly ProductionUnitContextReader _reader = reader;

    /// <inheritdoc />
    public async Task<ProductionUnitSnapshot?> FindAsync(string serial, CancellationToken cancellationToken)
    {
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
