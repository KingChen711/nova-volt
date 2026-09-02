using System.Buffers;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.Simulator.Publishing;

/// <summary>Publish lên EMQX qua MQTT, giống như một edge node thật sẽ làm.</summary>
/// <remarks>
/// <para>
/// QoS 1, at least once. QoS 0 sẽ làm mất reading khi có sự cố nhỏ, còn QoS 2 sẽ phải trả giá cho một
/// cái bắt tay exactly-once mà hệ thống không cần: mọi thứ ở downstream đã deduplicate dựa trên
/// <c>source_event_id</c> rồi (docs/scope.md §7.2), nên một lần gửi lặp chỉ tốn một insert lãng phí,
/// còn một lần mất thì tốn một measurement không ai khôi phục được.
/// </para>
/// <para>
/// Không có retained message. Một Sparkplug payload được retain sẽ bị phát lại cho mọi subscriber mới
/// như thể nó vừa mới được đo, trong khi <c>seq</c> của nó lại thuộc về một session đã kết thúc.
/// </para>
/// </remarks>
public sealed partial class MqttSparkplugPublisher : ISparkplugPublisher
{
    /// <summary>Metric mà một host set để yêu cầu node này tự khai báo lại chính nó.</summary>
    public const string RebirthControlMetric = "Node Control/Rebirth";

    private readonly IMqttClient _client;
    private readonly SimulatorOptions _settings;
    private readonly SparkplugTopic _deathTopic;
    private readonly ILogger<MqttSparkplugPublisher> _logger;
    private readonly string _broker;
    private readonly string _commandTopic;
    private MqttClientOptions _options;
    private bool _closing;

    // Mở trong khi một session tồn tại, đóng lại kể từ lúc một session rớt cho tới khi session kế
    // tiếp lên.
    //
    // Nếu không có cái này, PublishAsync sẽ với tới client bất cứ khi nào nhà máy có điều gì cần nói,
    // và trong lúc reconnect thì client đó không được kết nối: MQTTnet ném exception, exception đó đi
    // ngược lên qua tick loop của worker, và run kết thúc. Một sợi cáp bị cắt làm dừng cả line chính
    // là điều mà N15 cấm - nhà máy không quan tâm việc MES bị mất broker.
    //
    // Là một cái cổng (gate) chứ không phải một retry, vì thứ đang được chờ là một session chứ không
    // phải một message. Reconnect loop bên dưới chính là nơi mở cổng đó, và nó mở TRƯỚC khi khai báo
    // lại node: worker có thể đang giữ publishing lock của nó trong lúc bị chặn ở đây, và việc khai
    // báo cần đúng lock đó.
    private TaskCompletionSource _session = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Tạo publisher mà chưa connect.</summary>
    /// <param name="options">Broker ở đâu và node này tự xưng là gì.</param>
    /// <param name="logger">Log.</param>
    public MqttSparkplugPublisher(SimulatorOptions options, ILogger<MqttSparkplugPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _settings = options;
        _broker = $"{options.BrokerHost}:{options.BrokerPort}";
        _client = new MqttClientFactory().CreateMqttClient();

        var linePath = EquipmentPath.Parse(options.LinePath);
        _deathTopic = SparkplugTopic.For(linePath, SparkplugMessageType.NodeDeath);
        _commandTopic = SparkplugTopic.For(linePath, SparkplugMessageType.NodeCommand).Value;
        _client.ApplicationMessageReceivedAsync += CommandReceivedAsync;
        _client.DisconnectedAsync += ReconnectAsync;

        _options = SessionOptions(options.BirthDeathSequence);
    }

    // Dựng lại cho mỗi session vì will mang bdSeq, và will được cố định tại CONNECT.
    private MqttClientOptions SessionOptions(ulong birthDeathSequence) =>
        new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.BrokerHost, _settings.BrokerPort)
            .WithClientId($"nvm-simulator-{_settings.LinePath.Replace('/', '-')}")
            .WithCleanSession()
            // Last will được đăng ký tại CONNECT và được broker publish khi client này ngừng trả
            // lời. Đó chính là toàn bộ giá trị của nó: một process bị kill, hoặc một sợi cáp bị rút,
            // không có cơ hội để nói lời tạm biệt — nên lời tạm biệt đó được để lại cho broker từ
            // trước. Nếu không có nó, một node đã chết và một node đang im lặng sẽ trông giống hệt
            // nhau ở downstream, và report-by-exception khiến "im lặng" trở thành hoàn toàn bình
            // thường.
            .WithWillTopic(_deathTopic.Value)
            .WithWillPayload(DeathPayload(birthDeathSequence))
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(false)
            .Build();

    // MQTTnet raise cái này cho mọi lần đóng, kể cả của chính ta, nên một shutdown chủ động không
    // được phép bị đáp lại bằng việc quay số kết nối lại.
    private async Task ReconnectAsync(MqttClientDisconnectedEventArgs arguments)
    {
        if (_closing)
        {
            return;
        }

        // Đóng trước tiên, trước khi bất kỳ thứ gì được await. Một publish đến sau thời điểm này sẽ
        // không có session nào để đi trên đó và chờ session kế tiếp thay vì làm fail cả run.
        Volatile.Write(ref _session, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        ConnectionLost(_logger, arguments.Exception, _broker, _settings.ReconnectDelay);

        while (!_closing)
        {
            try
            {
                await Task.Delay(_settings.ReconnectDelay).ConfigureAwait(false);

                if (_closing)
                {
                    return;
                }

                // Một connection mới là một Sparkplug session mới, nên nó nhận một bdSeq mới trước
                // khi will mang số đó được đăng ký.
                if (BeginSession is { } beginSession)
                {
                    _options = SessionOptions(beginSession());
                }

                await ConnectAsync(CancellationToken.None).ConfigureAwait(false);

                if (SessionRestored is { } restored)
                {
                    await restored(CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Nhà máy không dừng lại vì broker không thể liên lạc được (N15). Cứ tiếp tục thử.
                ReconnectFailed(_logger, exception, _broker, _settings.ReconnectDelay);
            }
        }
    }

    // Chỉ có bdSeq, không gì khác. Will được soạn trước khi session bắt đầu, nên nó không thể mang
    // một reading — và nó chỉ tên session mà nó thuộc về, đó chính là điều ngăn một will đến trễ giết
    // chết session đã thay thế nó.
    private static byte[] DeathPayload(ulong birthDeathSequence) =>
        SparkplugPayload.EncodeData(
            [
                new DeviceReading(
                    SparkplugPayload.BirthDeathSequenceMetric,
                    Alias: null,
                    new MetricValue.Integral((long)birthDeathSequence),
                    DateTimeOffset.UnixEpoch),
            ],
            sequence: 0,
            DateTimeOffset.UnixEpoch);

    /// <inheritdoc />
    public Func<CancellationToken, Task>? RebirthRequested { get; set; }

    /// <inheritdoc />
    public Func<ulong>? BeginSession { get; set; }

    /// <inheritdoc />
    public Func<CancellationToken, Task>? SessionRestored { get; set; }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);

        // Subscribe trước khi birth đầu tiên được publish. Một gateway lên sau khi node này đã lên sẽ
        // không có cách nào đọc dù chỉ một message alias-only cho tới DBIRTH kế tiếp, mà trên một
        // formation line thì đó là cách một lần đổi cell — mười tám giờ. NCMD chính là cách nó báo
        // điều đó, và một node không lắng nghe sẽ biến một race lúc khởi động chỉ một giây thành một
        // khoảng mù kéo dài cả run.
        await _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter
                    .WithTopic(_commandTopic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(),
            cancellationToken).ConfigureAwait(false);

        // Mở ở đây thay vì sau khi node đã tự khai báo lại, và thứ tự này mang tính quyết định: caller
        // tự khai báo lại bằng cách publish, nên một cổng còn đóng cho tới khi các birth đã ra ngoài
        // sẽ là một cổng mà chính các birth đó không thể đi qua được.
        Volatile.Read(ref _session).TrySetResult();

        Connected(_logger, _broker, _options.ClientId);
    }

    private async Task CommandReceivedAsync(MqttApplicationMessageReceivedEventArgs arguments)
    {
        var handler = RebirthRequested;

        if (handler is null
            || !string.Equals(arguments.ApplicationMessage.Topic, _commandTopic, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            // Biến cục bộ có kiểu tường minh trước tiên: System.Collections.Immutable đang trong
            // scope ở đây và extension ToArray của nó khiến lời gọi trên một ReadOnlySequence trở nên
            // mập mờ.
            ReadOnlySequence<byte> payload = arguments.ApplicationMessage.Payload;
            var readings = SparkplugPayload.DecodeData(payload.ToArray(), MetricAliasTable.Empty);

            // Metric có tên, không alias: một command đến trước khi bất kỳ birth nào của ta kịp khai
            // báo alias, nên host phải viết rõ tên ra và ta phải đọc nó theo tên.
            var asked = readings.Any(reading =>
                string.Equals(reading.MetricName, RebirthControlMetric, StringComparison.Ordinal)
                && reading.Value is MetricValue.Flag { Value: true });

            if (asked)
            {
                await handler(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Một command mà ta không đọc được không phải là lý do để ngừng tạo dữ liệu. Nhà máy vẫn
            // tiếp tục chạy; consumer đã hỏi sẽ hỏi lại vào lần có gap kế tiếp của nó.
            CommandNotUnderstood(_logger, exception, arguments.ApplicationMessage.Topic);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the command on {Topic}")]
    private static partial void CommandNotUnderstood(ILogger logger, Exception exception, string topic);

    // Được source-generate thay vì gọi LogInformation trực tiếp: các argument chỉ được format khi
    // level đang bật, và không có gì bị box trên đường đi. CA1873 là quy tắc yêu cầu điều này.
    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {Broker} as {ClientId}")]
    private static partial void Connected(ILogger logger, string broker, string clientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Lost the connection to {Broker}; reconnecting in {Delay}")]
    private static partial void ConnectionLost(
        ILogger logger,
        Exception? exception,
        string broker,
        TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not reach {Broker}; retrying in {Delay}")]
    private static partial void ReconnectFailed(
        ILogger logger,
        Exception exception,
        string broker,
        TimeSpan delay);

    /// <inheritdoc />
    public async Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Từ chối thay vì giữ lại chờ. Chờ ở đây sẽ chặn một caller đang giữa chừng một batch, và khi
        // session cuối cùng cũng mở ra, nó sẽ đặt một message được đánh số bởi session ĐÃ CHẾT lên
        // đường truyền trước cả NBIRTH của session mới - một seq cũ nằm trước cả khai báo, đây chính
        // là hình dạng duy nhất mà một consumer không thể hiểu nổi.
        if (!Volatile.Read(ref _session).Task.IsCompleted)
        {
            throw new SparkplugPublishException(
                $"No session on {_broker}, so '{message.Topic.Value}' has nowhere to go.");
        }

        var mqtt = new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic.Value)
            .WithPayload(message.Payload.AsSpan().ToArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await _client.PublishAsync(mqtt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Cái cổng ở trên chỉ thu hẹp khoảng hở này; nó không thể đóng hẳn được. Một đường truyền
            // chết đúng vào khoảnh khắc giữa "có một session đang mở" và "gói tin đã lên đường truyền"
            // sẽ khiến client ném ra bất cứ thứ gì MQTTnet ném, và caller phải phân biệt được điều đó
            // với việc nhà máy đang sai - đó chính là lý do có cái wrapper này.
            throw new SparkplugPublishException(
                $"Could not publish to '{message.Topic.Value}' on {_broker}.",
                exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Không hứa hẹn rằng session vẫn còn đó khi việc chờ trả về: đường truyền có thể chết đúng vào
    /// khoảnh khắc đó. Race đó là không thể tránh khỏi và đó là lý do một caller vẫn phải chịu được
    /// một lần publish thất bại — điều mà cái cổng loại bỏ là khoảng thời gian lớn hơn nhiều, trong đó
    /// mọi publish trong lúc reconnect chắc chắn sẽ thất bại.
    /// </remarks>
    public async Task WaitForSessionAsync(CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref _session);

        if (session.Task.IsCompleted)
        {
            return;
        }

        await session.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _closing = true;

        // Bất cứ thứ gì đang bị chặn chờ một session đều được đánh thức thay vì bị bỏ lại đó. Một run
        // kết thúc trong khi broker không thể liên lạc được vẫn cần một report để ghi, và một shutdown
        // treo trên một cổng không ai từng mở còn tệ hơn một publish thất bại và báo lỗi rõ ràng.
        Volatile.Read(ref _session).TrySetResult();

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync().ConfigureAwait(false);
        }

        _client.Dispose();
    }
}
