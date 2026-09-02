using System.Diagnostics.Metrics;
using Nvm.EdgeGateway.Buffering;

namespace Nvm.EdgeGateway.Sessions;

/// <summary>Công bố những gì gateway tin là đúng về liveness của device.</summary>
/// <remarks>
/// <c>gateway.node.stale</c> là con số duy nhất một operator cần trong một outage, và cũng là con
/// số mà một đồ thị depth không thể đưa ra: một buffer đầy dần nói rằng backend không thể liên lạc
/// được, trong khi một node trở nên stale nói rằng một cái máy không thể liên lạc được. Đó là hai
/// lời cảnh báo khác nhau, gửi tới hai nhóm người khác nhau.
/// </remarks>
public sealed class GatewaySessionMetrics : IDisposable
{
    private readonly Meter _meter = new(GatewayBufferMetrics.MeterName);

    /// <summary>Đăng ký các instrument về liveness và rebirth.</summary>
    /// <param name="tracker">Nguồn của bức tranh node trực tiếp (live).</param>
    /// <param name="counters">Process counter đứng sau các tổng số rebirth.</param>
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
