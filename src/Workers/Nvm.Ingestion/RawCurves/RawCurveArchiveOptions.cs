namespace Nvm.Ingestion.RawCurves;

/// <summary>Where the original bytes of a machine export are kept.</summary>
/// <remarks>
/// Off by default because the archive belongs to file-drop ingestion. Program startup fails when
/// file drop is enabled without it; MQTT-only ingestion can run without an object store because it
/// never consumes a machine-export file whose original bytes must be retained.
/// </remarks>
public sealed class RawCurveArchiveOptions
{
    /// <summary>Whether originals are archived at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>S3 endpoint. MinIO inside the compose network, an S3 region endpoint elsewhere.</summary>
    public string ServiceUrl { get; set; } = "http://minio:9000";

    /// <summary>Access key. Supplied through the environment, never checked in (K13).</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret key. Supplied through the environment, never checked in (K13).</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>The object-locked bucket. Must already carry COMPLIANCE retention.</summary>
    public string BucketName { get; set; } = RawCurveArchiveStore.DefaultBucketName;

    /// <summary>Refuses a configuration that would fail on the first file rather than at startup.</summary>
    /// <exception cref="InvalidOperationException">Enabled without an endpoint or credentials.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ServiceUrl)
            || string.IsNullOrWhiteSpace(AccessKey)
            || string.IsNullOrWhiteSpace(SecretKey)
            || string.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException(
                "NVM_INGEST:RawCurveArchive is enabled but is missing an endpoint, a bucket or "
                + "credentials. Failing here is deliberate: the alternative is a service that accepts "
                + "files for hours and only discovers it cannot keep their originals when someone "
                + "asks for one.");
        }
    }
}
