namespace Nvm.EdgeGateway;

/// <summary>Process counters around MQTT acceptance and durable forwarding.</summary>
public sealed class GatewayCounters
{
    private long _decodedMessages;
    private long _bufferedMessages;
    private long _forwardedMessages;
    private long _rejectedMessages;
    private long _bufferFullEvents;
    private long _throttledFlushes;
    private long _rateLimitedFlushes;

    /// <summary>Birth/data messages decoded successfully.</summary>
    public long DecodedMessages => Interlocked.Read(ref _decodedMessages);

    /// <summary>Messages fsynced before MQTT acknowledgement.</summary>
    public long BufferedMessages => Interlocked.Read(ref _bufferedMessages);

    /// <summary>Messages accepted by ingestion and crossed by the durable cursor.</summary>
    public long ForwardedMessages => Interlocked.Read(ref _forwardedMessages);

    /// <summary>Malformed or unknown-site messages refused before forwarding.</summary>
    public long RejectedMessages => Interlocked.Read(ref _rejectedMessages);

    /// <summary>Times the hard disk cap stopped MQTT acceptance.</summary>
    public long BufferFullEvents => Interlocked.Read(ref _bufferFullEvents);

    /// <summary>Times ingestion answered 429/503 and the gateway slowed down instead of retrying.</summary>
    public long ThrottledFlushes => Interlocked.Read(ref _throttledFlushes);

    /// <summary>Times the gateway's own rate limiter held a batch back.</summary>
    /// <remarks>
    /// Kept apart from <see cref="ThrottledFlushes"/> because lab §5.C10.3 exists to tell the two
    /// causes apart: "we paced ourselves" and "the server made us" produce the same slow drain and
    /// completely different conclusions about whether ADR-029 earns its place.
    /// </remarks>
    public long RateLimitedFlushes => Interlocked.Read(ref _rateLimitedFlushes);

    internal long CountDecoded() => Interlocked.Increment(ref _decodedMessages);

    internal long CountBuffered(int count = 1) => Interlocked.Add(ref _bufferedMessages, count);

    internal long CountForwarded(int count = 1) => Interlocked.Add(ref _forwardedMessages, count);

    internal long CountRejected() => Interlocked.Increment(ref _rejectedMessages);

    internal long CountBufferFull() => Interlocked.Increment(ref _bufferFullEvents);

    internal long CountThrottled() => Interlocked.Increment(ref _throttledFlushes);

    internal long CountRateLimited() => Interlocked.Increment(ref _rateLimitedFlushes);
}
