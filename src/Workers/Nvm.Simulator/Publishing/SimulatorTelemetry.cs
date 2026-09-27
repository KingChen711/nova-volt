using System.Diagnostics;

namespace Nvm.Simulator.Publishing;

/// <summary>Nguồn trace của simulator: mỗi publish MQTT là gốc một trace (M13).</summary>
internal static class SimulatorTelemetry
{
    public static readonly ActivitySource Source = new("NovaVolt.Simulator");
}
