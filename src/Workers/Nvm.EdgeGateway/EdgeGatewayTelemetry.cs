using System.Diagnostics;

namespace Nvm.EdgeGateway;

/// <summary>Nguồn trace của edge gateway (M13).</summary>
internal static class EdgeGatewayTelemetry
{
    public static readonly ActivitySource Source = new("NovaVolt.EdgeGateway");
}
