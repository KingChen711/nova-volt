namespace Nvm.EdgeGateway;

/// <summary>Process counters that make loss in C08 visible instead of implicit.</summary>
public sealed class GatewayCounters
{
    private long _decodedMessages;
    private long _forwardedMessages;
    private long _droppedMessages;
    private long _rejectedMessages;

    /// <summary>Birth/data messages decoded successfully.</summary>
    public long DecodedMessages => Interlocked.Read(ref _decodedMessages);

    /// <summary>Messages accepted by ingestion.</summary>
    public long ForwardedMessages => Interlocked.Read(ref _forwardedMessages);

    /// <summary>Decoded messages lost because C08 has no store-and-forward yet.</summary>
    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);

    /// <summary>Malformed or unknown-site messages refused before forwarding.</summary>
    public long RejectedMessages => Interlocked.Read(ref _rejectedMessages);

    internal long CountDecoded() => Interlocked.Increment(ref _decodedMessages);

    internal long CountForwarded() => Interlocked.Increment(ref _forwardedMessages);

    internal long CountDropped() => Interlocked.Increment(ref _droppedMessages);

    internal long CountRejected() => Interlocked.Increment(ref _rejectedMessages);
}
