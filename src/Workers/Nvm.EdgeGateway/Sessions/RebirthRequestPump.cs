using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Sessions;

/// <summary>Publish <c>NCMD</c> yêu cầu một edge node khai báo lại chính nó.</summary>
/// <remarks>
/// <para>
/// Dùng một pump riêng thay vì publish ngay bên trong receive callback. MQTTnet dispatch các
/// callback đó tuần tự, nên publish từ bên trong một callback sẽ đặt một network round trip lên
/// đường đi của mọi message phía sau nó — và message đã kích hoạt rebirth, theo định nghĩa, đang
/// đến giữa lúc có sự cố.
/// </para>
/// <para>
/// Cố tình thiết kế theo kiểu best effort. Một rebirth request không publish được không phải là
/// mất dữ liệu: buffer vẫn giữ mọi thứ đã nhận, và gap tiếp theo sẽ yêu cầu lại. Làm sập host vì
/// điều này sẽ biến một bức tranh suy giảm thành một outage.
/// </para>
/// </remarks>
public sealed partial class RebirthRequestPump : BackgroundService
{
    /// <summary>Metric mà một node theo dõi để biết nó phải republish lại các birth của mình.</summary>
    public const string RebirthControlMetric = "Node Control/Rebirth";

    private readonly NodeSessionTracker _tracker;
    private readonly GatewaySessionMetrics _metrics;
    private readonly IMqttClient _mqtt;
    private readonly TimeProvider _clock;
    private readonly ILogger<RebirthRequestPump> _logger;

    /// <summary>Tạo pump trên hàng đợi pending-request của tracker.</summary>
    /// <param name="tracker">Nơi các rebirth request được raise.</param>
    /// <param name="metrics">
    /// Các instrument của session. Được nhận vào đây để container build chúng: một observable gauge
    /// mà không ai resolve tới là một gauge không bao giờ report, và nó sẽ fail một cách âm thầm.
    /// </param>
    /// <param name="mqtt">Kết nối broker của gateway.</param>
    /// <param name="clock">Đồng hồ đóng dấu command payload (K1).</param>
    /// <param name="logger">Structured log sink.</param>
    public RebirthRequestPump(
        NodeSessionTracker tracker,
        GatewaySessionMetrics metrics,
        IMqttClient mqtt,
        TimeProvider clock,
        ILogger<RebirthRequestPump> logger)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(mqtt);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _tracker = tracker;
        _metrics = metrics;
        _mqtt = mqtt;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var address in _tracker.RebirthRequests.ReadAllAsync(stoppingToken))
        {
            if (!_mqtt.IsConnected)
            {
                // Không có gì để làm và không có gì để báo cáo như một lỗi: một gateway bị ngắt kết
                // nối đã được worker ghi log rồi, và dù sao node cũng không nghe được ta.
                continue;
            }

            try
            {
                await PublishAsync(address, stoppingToken);
                RebirthRequested(_logger, address.GroupId, address.EdgeNodeId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RebirthRequestFailed(_logger, exception, address.GroupId, address.EdgeNodeId);
            }
        }
    }

    private async Task PublishAsync(NodeAddress address, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        // seq 0: một command không thuộc về stream đánh số riêng của node, và một consumer đang đếm
        // stream đó không được thấy traffic của ta lẫn trong đó.
        var payload = SparkplugPayload.EncodeData(
            [new DeviceReading(RebirthControlMetric, Alias: null, new MetricValue.Flag(true), now)],
            sequence: 0,
            now);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(address.NodeCommandTopic().Value)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqtt.PublishAsync(message, cancellationToken);
    }

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Warning,
        Message = "Requested rebirth from {GroupId}/{EdgeNodeId} after a sequence gap")]
    private static partial void RebirthRequested(ILogger logger, string groupId, string edgeNodeId);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Warning,
        Message = "Could not publish a rebirth request to {GroupId}/{EdgeNodeId}; the next gap will ask again")]
    private static partial void RebirthRequestFailed(
        ILogger logger,
        Exception exception,
        string groupId,
        string edgeNodeId);
}
