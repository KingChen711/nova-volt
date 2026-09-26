using Nvm.Kernel.Identity;
using Nvm.PublicObjectModel;
using Nvm.Quality.Ports;

namespace Nvm.Quality.Hosting;

/// <summary>Unit hạ nguồn đọc từ read model genealogy (closure + span).</summary>
public sealed class TraceDownstreamUnits(TraceQueries trace) : IDownstreamUnits
{
    public Task<IReadOnlyList<string>> ReadAsync(string siteId, string targetKind, string targetId, decimal? spanFromMeter,
        decimal? spanToMeter, CancellationToken cancellationToken)
    {
        short type = targetKind switch
        {
            "Lot" => 1,
            "Roll" => 5,
            "Unit" => SerialNumber.Parse(targetId).Kind switch
            {
                ProductionUnitKind.Cell => 2,
                ProductionUnitKind.Module => 3,
                _ => 4
            },
            _ => throw new ArgumentException("Unknown hold target kind.", nameof(targetKind))
        };
        return trace.DownstreamUnitsAsync(siteId, type, targetId, spanFromMeter, spanToMeter, cancellationToken);
    }
}
