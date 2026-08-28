using System.Buffers;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.Simulator.Publishing;

/// <summary>Publishes to EMQX over MQTT, as a real edge node would.</summary>
/// <remarks>
/// <para>
/// QoS 1, at least once. QoS 0 would lose readings on a blip and QoS 2 would pay for an exactly-once
/// handshake the system does not need: everything downstream deduplicates on
/// <c>source_event_id</c> already (docs/scope.md §7.2), so a repeat costs one wasted insert and a
/// loss costs a measurement nobody can recover.
/// </para>
/// <para>
/// No retained messages. A retained Sparkplug payload would be replayed to every new subscriber as
/// though it had just been measured, and its <c>seq</c> would be from a session that has ended.
/// </para>
/// </remarks>
public sealed partial class MqttSparkplugPublisher : ISparkplugPublisher
{
    /// <summary>The metric a host sets to ask this node to declare itself again.</summary>
    public const string RebirthControlMetric = "Node Control/Rebirth";

    private readonly IMqttClient _client;
    private readonly SimulatorOptions _settings;
    private readonly SparkplugTopic _deathTopic;
    private readonly ILogger<MqttSparkplugPublisher> _logger;
    private readonly string _broker;
    private readonly string _commandTopic;
    private MqttClientOptions _options;
    private bool _closing;

    // Open while a session exists, closed from the moment one drops until the next one is up.
    //
    // Without it, PublishAsync reaches for the client whenever the plant has something to say, and
    // during a reconnect that client is not connected: MQTTnet throws, the exception travels up
    // through the worker's tick loop, and the run ends. A cut cable stopping the line is exactly
    // what N15 forbids - the plant does not care that the MES lost its broker.
    //
    // A gate rather than a retry, because the thing being waited for is a session and not a
    // message. The reconnect loop below is what opens it, and it opens it BEFORE re-declaring the
    // node: the worker can be holding its publishing lock while parked here, and the declaration
    // needs that same lock.
    private TaskCompletionSource _session = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates the publisher without connecting.</summary>
    /// <param name="options">Where the broker is and who this node says it is.</param>
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

    // Rebuilt per session because the will carries bdSeq, and the will is fixed at CONNECT.
    private MqttClientOptions SessionOptions(ulong birthDeathSequence) =>
        new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.BrokerHost, _settings.BrokerPort)
            .WithClientId($"nvm-simulator-{_settings.LinePath.Replace('/', '-')}")
            .WithCleanSession()
            // The last will is registered at CONNECT and published by the broker when this client
            // stops answering. That is the whole value of it: a process that is killed, or a cable
            // that is pulled, gets no chance to say goodbye — so the goodbye is left with the broker
            // in advance. Without it, a dead node and a quiet node look identical downstream, and
            // report-by-exception makes "quiet" completely normal.
            .WithWillTopic(_deathTopic.Value)
            .WithWillPayload(DeathPayload(birthDeathSequence))
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(false)
            .Build();

    // MQTTnet raises this for every close, ours included, so a deliberate shutdown must not be
    // answered by dialling back in.
    private async Task ReconnectAsync(MqttClientDisconnectedEventArgs arguments)
    {
        if (_closing)
        {
            return;
        }

        // Closed first, before anything is awaited. A publish that arrives after this point has no
        // session to travel on and waits for the next one instead of failing the run.
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

                // A new connection is a new Sparkplug session, so it gets a new bdSeq before the
                // will that carries it is registered.
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
                // The plant does not stop because the broker is unreachable (N15). Keep trying.
                ReconnectFailed(_logger, exception, _broker, _settings.ReconnectDelay);
            }
        }
    }

    // bdSeq and nothing else. The will is composed before the session starts, so it cannot carry a
    // reading — and it names the session it belongs to, which is what stops a will delivered late
    // from killing the session that has already replaced it.
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

        // Subscribed before the first birth is published. A gateway that comes up after this node
        // has no way to read one alias-only message until the next DBIRTH, which on a formation line
        // is a cell change away — eighteen hours. NCMD is how it says so, and a node that is not
        // listening turns a one-second startup race into a run-long blackout.
        await _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter
                    .WithTopic(_commandTopic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(),
            cancellationToken).ConfigureAwait(false);

        // Opened here rather than after the node has re-declared itself, and the ordering is load
        // bearing: the caller re-declares by publishing, so a gate that stayed shut until the births
        // were out would be a gate the births could not get through.
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
            // Typed local first: System.Collections.Immutable is in scope here and its ToArray
            // extension makes the call on a ReadOnlySequence ambiguous.
            ReadOnlySequence<byte> payload = arguments.ApplicationMessage.Payload;
            var readings = SparkplugPayload.DecodeData(payload.ToArray(), MetricAliasTable.Empty);

            // Named metric, no alias: a command arrives before any birth of ours could have declared
            // one, so the host has to spell it out and we have to read it by name.
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
            // A command we cannot read is not a reason to stop producing data. The plant keeps
            // running; the consumer that asked will ask again on its next gap.
            CommandNotUnderstood(_logger, exception, arguments.ApplicationMessage.Topic);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the command on {Topic}")]
    private static partial void CommandNotUnderstood(ILogger logger, Exception exception, string topic);

    // Source-generated rather than a LogInformation call: the arguments are formatted only when the
    // level is enabled, and nothing is boxed on the way in. CA1873 is what asks for this.
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

        // Refused rather than held. Waiting here would park a caller that is part way through a
        // batch, and when the session finally opened it would put a message numbered by the DEAD
        // session on the wire ahead of the new session's NBIRTH - one stale seq in front of the
        // declaration, which is the one shape a consumer cannot make sense of.
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
            // The gate above narrows this window; it cannot close it. A link that dies in the moment
            // between "a session is open" and "the packet is on the wire" leaves the client throwing
            // whatever MQTTnet throws, and the caller has to be able to tell that apart from the
            // plant being wrong - which is what the wrapper is for.
            throw new SparkplugPublishException(
                $"Could not publish to '{message.Topic.Value}' on {_broker}.",
                exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Does not promise a session is still there when the wait returns: the link can die in the
    /// moment between. That race is unavoidable and is why a caller still has to survive a failed
    /// publish — what the gate removes is the far larger window in which every publish during a
    /// reconnect was certain to fail.
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

        // Anything parked waiting for a session is woken rather than left there. A run that ends
        // while the broker is unreachable still has a report to write, and a shutdown that hangs on
        // a gate nobody will ever open is worse than a publish that fails and says so.
        Volatile.Read(ref _session).TrySetResult();

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync().ConfigureAwait(false);
        }

        _client.Dispose();
    }
}
