using System.Diagnostics.Metrics;
using Nvm.EdgeGateway.Buffering;

namespace Nvm.EdgeGateway.Sessions;

/// <summary>Publishes what the gateway believes about device liveness.</summary>
/// <remarks>
/// <c>gateway.node.stale</c> is the one number an operator wants during an outage and the one a
/// depth graph cannot give: a buffer filling up says the backend is unreachable, while a node going
/// stale says a machine is. Those are different call-outs to different people.
/// </remarks>
public sealed class GatewaySessionMetrics : IDisposable
{
    private readonly Meter _meter = new(GatewayBufferMetrics.MeterName);

    /// <summary>Registers the liveness and rebirth instruments.</summary>
    /// <param name="tracker">Source of the live node picture.</param>
    /// <param name="counters">Process counters behind the rebirth totals.</param>
    public GatewaySessionMetrics(NodeSessionTracker tracker, GatewayCounters counters)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(counters);

        _meter.CreateObservableGauge("gateway.node.stale", () => tracker.StaleNodeCount, unit: "{node}");
        _meter.CreateObservableCounter(
            "gateway.rebirth.requests",
            () => counters.RebirthRequests,
            unit: "{request}");
        _meter.CreateObservableCounter(
            "gateway.node.late_deaths_ignored",
            () => counters.LateDeathsIgnored,
            unit: "{death}");
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
