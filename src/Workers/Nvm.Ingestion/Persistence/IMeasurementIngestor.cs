using Nvm.Sparkplug;

namespace Nvm.Ingestion.Persistence;

/// <summary>The one dedup path shared by every device adapter.</summary>
public interface IMeasurementIngestor
{
    /// <summary>Claims natural keys and stores their readings in one database transaction.</summary>
    Task<IngestionResult> IngestAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken);
}
