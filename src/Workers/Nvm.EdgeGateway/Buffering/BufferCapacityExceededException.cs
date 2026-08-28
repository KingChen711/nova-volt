namespace Nvm.EdgeGateway.Buffering;

/// <summary>The durable edge queue reached its configured disk boundary.</summary>
public sealed class BufferCapacityExceededException : IOException
{
    /// <summary>Creates a hard-stop result that reports measured and configured bytes.</summary>
    public BufferCapacityExceededException(long bytesOnDisk, long bytesRequested, long maxBytes)
        : base(
            $"Store-and-forward holds {bytesOnDisk} bytes and cannot append {bytesRequested}: "
            + $"the configured limit is {maxBytes}. New MQTT deliveries must stop; old records are never overwritten.")
    {
        BytesOnDisk = bytesOnDisk;
        BytesRequested = bytesRequested;
        MaxBytes = maxBytes;
    }

    /// <summary>Data-file bytes present before the refused append.</summary>
    public long BytesOnDisk { get; }

    /// <summary>Framed bytes the caller tried to add.</summary>
    public long BytesRequested { get; }

    /// <summary>The configured hard boundary.</summary>
    public long MaxBytes { get; }
}
