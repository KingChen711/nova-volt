using System.Diagnostics;

namespace Nvm.Ingestion.Persistence;

/// <summary>Nguồn trace của ingestion (M13).</summary>
public static class IngestionTelemetry
{
    public const string Name = "NovaVolt.Ingestion";

    public static readonly ActivitySource Source = new(Name);
}
