using System.Buffers;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.EdgeGateway.Buffering;
using Nvm.EdgeGateway.Decoding;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway;

/// <summary>Subscribe tới Sparkplug namespace và chuyển các batch đã decode về phía ingestion.</summary>
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

    /// <summary>Tạo DMZ subscriber mà chưa kết nối vội.</summary>
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

    /// <summary>Xây dựng session MQTT 5 persistent mà runtime worker sử dụng.</summary>
    internal static MqttClientOptions BuildMqttOptions(EdgeGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new MqttClientOptionsBuilder()
            .WithTcpServer(options.BrokerHost, options.BrokerPort)
            .WithClientId(options.ClientId)
            // MQTT 5 cần cả Clean Start=false lẫn một expiry khác zero. Thiếu trường thứ hai, EMQX
            // báo cáo is_persistent=false và hủy session QoS 1 chưa được acknowledge.
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
                    // Sức chứa có thể phục hồi khi flusher đẩy cursor tiến lên. Một lỗi I/O bất kỳ
                    // không thể chứng minh là tạm thời, nên chỉ một lần operator restart mới được
                    // phép retry nó.
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
                // Được gác bởi stopping token, không phải bởi loại exception: một MQTT connect bị
                // timeout sẽ hiện ra dưới dạng TaskCanceledException, thứ kế thừa từ
                // OperationCanceledException. Xem StoreAndForwardFlusher để biết cái giá của điều đó.
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
            // Một kết nối bị mất làm cờ subscription cục bộ của ta không còn hợp lệ, dù EMQX vẫn
            // giữ lại persistent session cùng các delivery QoS 1 chưa được acknowledge của nó.
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
        // QoS 1 chưa durable cho tới khi ổ đĩa của ta durable. Auto-ack mặc định của MQTTnet sẽ để
        // EMQX quên message trước khi fsync hoàn tất, để lại một khoảng hở mất điện mà không test
        // nào có thể đối soát được.
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

            // Một publish sai định dạng/site không xác định là poison, không phải một outage tạm
            // thời. Để nó không được acknowledge sẽ ghim persistent session vào đúng những byte
            // không đọc được đó mãi mãi.
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

            // Không await fsync bên trong MQTT callback. MQTTnet dispatch các callback tuần tự;
            // làm vậy sẽ buộc mỗi publish phải có một lần flush đĩa vật lý riêng. Bounded channel
            // cho phép đủ số delivery QoS 1 đang chờ để chia sẻ chung một fsync, trong khi mỗi ACK
            // vẫn chờ nó hoàn tất.
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
            // Một lỗi I/O đĩa không phải là một MQTT message poison. Không ACK nó và dừng nhận thêm
            // traffic; một lần restart/recovery an toàn hơn là âm thầm tạo ra một lỗ hổng.
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
