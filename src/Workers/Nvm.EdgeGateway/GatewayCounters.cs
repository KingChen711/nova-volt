namespace Nvm.EdgeGateway;

/// <summary>Process counters around MQTT acceptance and durable forwarding.</summary>
public sealed class GatewayCounters
{
    private long _decodedMessages;
    private long _bufferedMessages;
    private long _forwardedMessages;
    private long _rejectedMessages;
    private long _bufferFullEvents;

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

    internal long CountDecoded() => Interlocked.Increment(ref _decodedMessages);

    internal long CountBuffered(int count = 1) => Interlocked.Add(ref _bufferedMessages, count);

    internal long CountForwarded(int count = 1) => Interlocked.Add(ref _forwardedMessages, count);

    internal long CountRejected() => Interlocked.Increment(ref _rejectedMessages);

    internal long CountBufferFull() => Interlocked.Increment(ref _bufferFullEvents);
}
