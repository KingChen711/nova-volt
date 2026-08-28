using System.Buffers;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.EdgeGateway.Buffering;
using Nvm.EdgeGateway.Decoding;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway;

/// <summary>Subscribes to the Sparkplug namespace and hands decoded batches toward ingestion.</summary>
public sealed partial class EdgeGatewayWorker : BackgroundService
{
    private readonly EdgeGatewayOptions _options;
    private readonly IMqttClient _mqtt;
    private readonly SparkplugMessageDecoder _decoder;
    private readonly IGatewayBufferWriter _bufferWriter;
    private readonly FileStoreAndForwardBuffer _buffer;
    private readonly GatewayCounters _counters;
    private readonly TimeProvider _clock;
    private readonly ILogger<EdgeGatewayWorker> _logger;
    private readonly MqttClientOptions _mqttOptions;
    private CancellationToken _stoppingToken;
    private bool _subscribed;
    private volatile bool _bufferFull;
    private volatile bool _durableWriteFault;

    /// <summary>Creates the DMZ subscriber without connecting yet.</summary>
    public EdgeGatewayWorker(
        EdgeGatewayOptions options,
        IMqttClient mqtt,
        SparkplugMessageDecoder decoder,
        IGatewayBufferWriter bufferWriter,
        FileStoreAndForwardBuffer buffer,
        GatewayCounters counters,
        TimeProvider clock,
        ILogger<EdgeGatewayWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mqtt);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(bufferWriter);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _mqtt = mqtt;
        _decoder = decoder;
        _bufferWriter = bufferWriter;
        _buffer = buffer;
        _counters = counters;
        _clock = clock;
        _logger = logger;

        _mqttOptions = BuildMqttOptions(options);

        _mqtt.ApplicationMessageReceivedAsync += MessageReceivedAsync;
    }

    /// <summary>Builds the persistent MQTT 5 session used by the runtime worker.</summary>
    internal static MqttClientOptions BuildMqttOptions(EdgeGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new MqttClientOptionsBuilder()
            .WithTcpServer(options.BrokerHost, options.BrokerPort)
            .WithClientId(options.ClientId)
            // MQTT 5 needs both Clean Start=false and a non-zero expiry. Without the second field,
            // EMQX reports is_persistent=false and discards the unacknowledged QoS 1 session.
            .WithCleanSession(false)
            .WithSessionExpiryInterval(checked((uint)options.SessionExpiryInterval.TotalSeconds))
            .Build();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_bufferFull || _durableWriteFault)
            {
                if (_mqtt.IsConnected)
                {
                    await _mqtt.DisconnectAsync(cancellationToken: stoppingToken);
                    _subscribed = false;
                }

                if (_durableWriteFault)
                {
                    // Capacity can recover as the flusher advances the cursor. An arbitrary I/O
                    // failure cannot be proven transient, so only an operator restart may retry it.
                    await Task.Delay(_options.Buffer.FlushRetryDelay, _clock, stoppingToken);
                    continue;
                }

                if (_buffer.Snapshot.HasWriteRoom)
                {
                    _bufferFull = false;
                    BufferAcceptanceResumed(_logger, _buffer.Snapshot.Depth, _buffer.Snapshot.Bytes);
                }
                else
                {
                    await Task.Delay(_options.Buffer.FlushRetryDelay, _clock, stoppingToken);
                    continue;
                }
            }

            if (!_mqtt.IsConnected || !_subscribed)
            {
                try
                {
                    await ConnectAndSubscribeAsync(stoppingToken);
                }
                // Guarded on the stopping token, not on the exception type: an MQTT connect that
                // times out surfaces as TaskCanceledException, which derives from
                // OperationCanceledException. See StoreAndForwardFlusher for what that cost.
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    BrokerUnavailable(_logger, exception, _options.ReconnectDelay);
                }
            }

            await Task.Delay(_options.ReconnectDelay, _clock, stoppingToken);
        }
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken)
    {
        if (!_mqtt.IsConnected)
        {
            // A lost connection invalidates our local subscription flag even though EMQX retains
            // the persistent session and its unacknowledged QoS 1 deliveries.
            _subscribed = false;
            await _mqtt.ConnectAsync(_mqttOptions, cancellationToken);
        }

        var subscription = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(filter => filter
                .WithTopic($"{SparkplugTopic.Namespace}/#")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();

        await _mqtt.SubscribeAsync(subscription, cancellationToken);
        _subscribed = true;
        Subscribed(_logger, _options.BrokerHost, _options.BrokerPort, $"{SparkplugTopic.Namespace}/#");
    }

    private async Task MessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arguments)
    {
        // QoS 1 is not durable until our disk is. MQTTnet's default auto-ack would let EMQX forget
        // the message before fsync completed, leaving a power-loss window no test could reconcile.
        arguments.AutoAcknowledge = false;

        DecodedSparkplugMessage? decoded;

        try
        {
            var applicationMessage = arguments.ApplicationMessage;
            ReadOnlySequence<byte> payload = applicationMessage.Payload;
            decoded = payload.IsSingleSegment
                ? _decoder.Decode(applicationMessage.Topic, payload.FirstSpan)
                : _decoder.Decode(applicationMessage.Topic, payload.ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var rejected = _counters.CountRejected();

            if (rejected == 1 || rejected % _options.LogEvery == 0)
            {
                MessageRejected(_logger, exception, rejected, arguments.ApplicationMessage.Topic);
            }

            // A malformed/unknown-site publish is poison, not a transient outage. Leaving it
            // unacknowledged would pin the persistent session on the same unreadable bytes forever.
            await arguments.AcknowledgeAsync(_stoppingToken);
            return;
        }

        if (decoded is null)
        {
            await arguments.AcknowledgeAsync(_stoppingToken);
            return;
        }

        var decodedCount = _counters.CountDecoded();

        if (decodedCount % _options.LogEvery == 0)
        {
            Progress(
                _logger,
                decodedCount,
                _counters.BufferedMessages,
                _counters.ForwardedMessages,
                _counters.RejectedMessages,
                _buffer.Snapshot.Depth);
        }

        try
        {
            var persisted = await _bufferWriter.QueueAsync([decoded], _stoppingToken);

            // Do not await fsync in the MQTT callback. MQTTnet dispatches callbacks serially; doing
            // so would force one physical disk flush per publish. The bounded channel admits enough
            // outstanding QoS 1 deliveries to share one fsync, while each ACK still waits for it.
            _ = CompleteDurableAcceptanceAsync(persisted, arguments);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            arguments.ProcessingFailed = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            arguments.ProcessingFailed = true;
            _durableWriteFault = true;
            DurableWriteFailed(_logger, exception, _buffer.Snapshot.Depth, _buffer.Snapshot.Bytes);
        }
    }

    private async Task CompleteDurableAcceptanceAsync(
        Task persisted,
        MqttApplicationMessageReceivedEventArgs arguments)
    {
        try
        {
            await persisted;
            _counters.CountBuffered();
            await arguments.AcknowledgeAsync(_stoppingToken);
        }
        catch (BufferCapacityExceededException exception)
        {
            arguments.ProcessingFailed = true;
            _bufferFull = true;
            var fullEvents = _counters.CountBufferFull();
            BufferFull(_logger, exception, fullEvents, _buffer.Snapshot.Depth, _buffer.Snapshot.Bytes);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            arguments.ProcessingFailed = true;
        }
        catch (Exception exception)
        {
            // A disk I/O failure is not a poison MQTT message. Do not ACK it and stop accepting
            // further traffic; a restart/recovery is safer than silently making a hole.
            arguments.ProcessingFailed = true;
            _durableWriteFault = true;
            DurableWriteFailed(_logger, exception, _buffer.Snapshot.Depth, _buffer.Snapshot.Bytes);
        }
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Subscribed to {BrokerHost}:{BrokerPort} topic {TopicFilter}")]
    private static partial void Subscribed(
        ILogger logger,
        string brokerHost,
        int brokerPort,
        string topicFilter);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "EMQX connect/subscribe failed; retrying after {ReconnectDelay}")]
    private static partial void BrokerUnavailable(ILogger logger, Exception exception, TimeSpan reconnectDelay);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "Gateway totals: decoded={Decoded}, buffered={Buffered}, forwarded={Forwarded}, rejected={Rejected}, buffer_depth={BufferDepth}")]
    private static partial void Progress(
        ILogger logger,
        long decoded,
        long buffered,
        long forwarded,
        long rejected,
        long bufferDepth);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Critical,
        Message = "Gateway buffer hit its hard cap ({FullEvents} events); MQTT acceptance stopped. depth={BufferDepth}, bytes={BufferBytes}")]
    private static partial void BufferFull(
        ILogger logger,
        Exception exception,
        long fullEvents,
        long bufferDepth,
        long bufferBytes);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Warning,
        Message = "Rejected {Rejected} MQTT messages; latest topic={Topic}")]
    private static partial void MessageRejected(ILogger logger, Exception exception, long rejected, string topic);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Warning,
        Message = "Gateway buffer has write room again; MQTT acceptance resumes. depth={BufferDepth}, bytes={BufferBytes}")]
    private static partial void BufferAcceptanceResumed(ILogger logger, long bufferDepth, long bufferBytes);

    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Critical,
        Message = "Durable gateway write failed; MQTT acceptance stopped. depth={BufferDepth}, bytes={BufferBytes}")]
    private static partial void DurableWriteFailed(
        ILogger logger,
        Exception exception,
        long bufferDepth,
        long bufferBytes);
}
