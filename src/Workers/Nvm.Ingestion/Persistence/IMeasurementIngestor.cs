using Nvm.Ingestion.FileDrop;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.Persistence;

/// <summary>The one dedup path shared by every device adapter.</summary>
/// <remarks>
/// Two entry points, one door. The adapters differ in how they <b>read</b> — protobuf off MQTT, text
/// off a share — and in nothing else: both build the natural key with the same call and both commit
/// through the same transaction. A second dedup definition would drift from the first within months,
/// and the symptom would be one measurement stored twice for exactly the machines that report
/// through both routes (C15.1).
/// </remarks>
public interface IMeasurementIngestor
{
    /// <summary>Claims natural keys and stores their readings in one database transaction.</summary>
    /// <param name="messages">Decoded Sparkplug messages from the edge gateway.</param>
    /// <param name="cancellationToken">Stops the work when the host shuts down.</param>
    Task<IngestionResult> IngestAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken);

    /// <summary>Stores measurements read from a dropped file, through the same claim and insert.</summary>
    /// <param name="measurements">Lines that parsed.</param>
    /// <param name="readAt">
    /// When ingestion read the file. Stored as <c>gateway_timestamp</c> — it is the first trustworthy
    /// clock the reading passed, which is exactly what that column means.
    /// </param>
    /// <param name="cancellationToken">Stops the work when the host shuts down.</param>
    Task<IngestionResult> IngestAsync(
        IReadOnlyCollection<FileMeasurement> measurements,
        DateTimeOffset readAt,
        CancellationToken cancellationToken);
}
