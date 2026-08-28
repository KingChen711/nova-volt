using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Sessions;

/// <summary>Publishes the <c>NCMD</c> that asks an edge node to declare itself again.</summary>
/// <remarks>
/// <para>
/// A separate pump rather than a publish inside the receive callback. MQTTnet dispatches those
/// callbacks serially, so publishing from one would put a network round trip on the path of every
/// message behind it — and the message that triggered the rebirth is, by definition, arriving during
/// trouble.
/// </para>
/// <para>
/// Best effort by design. A rebirth request that cannot be published is not data loss: the buffer
/// still holds everything received, and the next gap will ask again. Failing the host over it would
/// turn a degraded picture into an outage.
/// </para>
/// </remarks>
public sealed partial class RebirthRequestPump : BackgroundService
{
    /// <summary>The metric a node watches to know it must republish its births.</summary>
    public const string RebirthControlMetric = "Node Control/Rebirth";

    private readonly NodeSessionTracker _tracker;
    private readonly GatewaySessionMetrics _metrics;
    private readonly IMqttClient _mqtt;
    private readonly TimeProvider _clock;
    private readonly ILogger<RebirthRequestPump> _logger;

    /// <summary>Creates the pump over the tracker's pending-request queue.</summary>
    /// <param name="tracker">Where rebirth requests are raised.</param>
    /// <param name="metrics">
    /// The session instruments. Taken here so the container builds them: an observable gauge that
    /// nothing resolves is a gauge that never reports, and it would fail silently.
    /// </param>
    /// <param name="mqtt">The gateway's broker connection.</param>
    /// <param name="clock">Clock stamping the command payload (K1).</param>
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
                // Nothing to do and nothing to report as an error: a disconnected gateway is already
                // being logged by the worker, and the node cannot hear us either way.
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

        // seq 0: a command is not part of the node's own numbered stream, and a consumer counting
        // that stream must not see our traffic in it.
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
