using System.Diagnostics;

namespace Nvm.EventStore;

/// <summary>Span của event store. Tên nguồn có tiền tố "NovaVolt." để host bật bằng AddNvmObservability.</summary>
public static class EventStoreTelemetry
{
    public const string SourceName = "NovaVolt.EventStore";

    public static readonly ActivitySource Source = new(SourceName);
}
