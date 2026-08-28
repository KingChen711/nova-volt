namespace Nvm.EdgeGateway;

/// <summary>Addresses and timing for the OT-to-DMZ conduit.</summary>
public sealed class EdgeGatewayOptions
{
    /// <summary>EMQX service name in <c>dmz-net</c>.</summary>
    public string BrokerHost { get; set; } = "emqx";

    /// <summary>MQTT listener port inside the Docker network.</summary>
    public int BrokerPort { get; set; } = 1883;

    /// <summary>Identity visible to the broker.</summary>
    public string ClientId { get; set; } = "nvm-edge-gateway";

    /// <summary>The versioned HTTP endpoint owned by ingestion.</summary>
    public string IngestionUrl { get; set; } = "http://ingestion:8080/api/ingestion/v1/sparkplug-batches";

    /// <summary>How long one buffer flush may wait for ingestion.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Delay before reconnecting to EMQX.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long EMQX retains this MQTT 5 session while the gateway is disconnected.</summary>
    /// <remarks>
    /// MQTT 5 defaults this value to zero even when Clean Start is false. A zero value would discard
    /// every QoS 1 delivery still waiting for the post-fsync acknowledgement at disconnect time.
    /// </remarks>
    public TimeSpan SessionExpiryInterval { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Directory holding the immutable factory-model documents.</summary>
    public string SeedDirectory { get; set; } = "seed";

    /// <summary>Revision this gateway reads; null selects the newest published document.</summary>
    public int? Revision { get; set; }

    /// <summary>How many decoded messages pass between progress logs.</summary>
    public int LogEvery { get; set; } = 1000;

    /// <summary>Atomic operational snapshot consumed by fail-closed M2 labs.</summary>
    /// <remarks>
    /// Null places the file beside the durable segments. It is not telemetry or a business query;
    /// it is a one-process diagnostic needed to compare exact counters without scraping sampled
    /// logs.
    /// </remarks>
    public string? DiagnosticsPath { get; set; }

    /// <summary>Durable queue configuration.</summary>
    public Buffering.PersistentBufferOptions Buffer { get; set; } = new();

    /// <summary>The configured or derived diagnostics file.</summary>
    public string ResolvedDiagnosticsPath => DiagnosticsPath ?? Path.Combine(Buffer.DirectoryPath, "gateway.stats");

    /// <summary>The validated endpoint.</summary>
    public Uri IngestionEndpoint => new(IngestionUrl, UriKind.Absolute);

    /// <summary>Refuses a gateway configuration that cannot carry traffic.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SeedDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BrokerPort);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LogEvery);
        Buffer.Validate();

        if (DiagnosticsPath is not null && string.IsNullOrWhiteSpace(DiagnosticsPath))
        {
            throw new InvalidOperationException("A configured diagnostics path must not be blank.");
        }

        if (Revision is < 1)
        {
            throw new InvalidOperationException("A configured factory-model revision must be at least 1.");
        }

        if (RequestTimeout <= TimeSpan.Zero || ReconnectDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Request timeout and reconnect delay must both be positive.");
        }

        if (SessionExpiryInterval <= TimeSpan.Zero
            || SessionExpiryInterval.TotalSeconds > uint.MaxValue)
        {
            throw new InvalidOperationException(
                "MQTT session expiry must be positive and fit the protocol's uint32-second field.");
        }

        if (!Uri.TryCreate(IngestionUrl, UriKind.Absolute, out var endpoint)
            || (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"IngestionUrl '{IngestionUrl}' must be an absolute HTTP or HTTPS URL.");
        }
    }
}
