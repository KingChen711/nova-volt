using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Simulator;
using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>
/// R6. The dropout fault the simulator already had holds messages in RAM and releases them later —
/// useful, but it never closes a socket, so it cannot show the one behaviour that only exists
/// because the link can die: the broker speaking for a node that can no longer speak for itself.
///
/// This exercises the real <see cref="MqttSparkplugPublisher"/> against a real broker and cuts the
/// connection underneath it, the way a pulled cable would. Two things have to follow, and neither is
/// visible in a unit test: the broker publishes the last will as an <c>NDEATH</c>, and the node comes
/// back with a fresh <c>NBIRTH</c> under a new <c>bdSeq</c>.
/// </summary>
public sealed class SimulatorMqttDisconnectTests : IAsyncLifetime
{
    private const string LinePath = "NOVAVOLT/NV1/FORMATION/F1";
    private const ulong FirstSession = 7;

    private readonly IContainer _broker = new ContainerBuilder("eclipse-mosquitto:2.0.22")
        // The image ships this config precisely so a broker can be started without a password file.
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(1883, true)
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _broker.StartAsync(TestContext.Current.CancellationToken);
        await WaitForBrokerAsync(TestContext.Current.CancellationToken);
    }

    // Mosquitto is listening within a second, but "the container started" and "the port answers"
    // are not the same event, and connecting into the gap fails the test for the wrong reason.
    private async Task WaitForBrokerAsync(CancellationToken cancellationToken)
    {
        var port = _broker.GetMappedPublicPort(1883);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(_broker.Hostname, port, cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        throw new TimeoutException("The mosquitto container never accepted a connection.");
    }

    public async ValueTask DisposeAsync() => await _broker.DisposeAsync();

    [Fact]
    public async Task ACutConnection_PublishesTheWillAsNodeDeathAndComesBackWithANewSession()
    {
        var token = TestContext.Current.CancellationToken;
        var brokerPort = _broker.GetMappedPublicPort(1883);

        // The publisher reaches the broker through a proxy we control. Killing the proxy's sockets
        // is the only way to produce what a cut cable produces: no DISCONNECT packet. Asking MQTTnet
        // to disconnect would be a clean goodbye, and a clean goodbye tells the broker to DISCARD the
        // will — the exact case this test exists to rule out.
        await using var link = new CuttableLink(_broker.Hostname, brokerPort);

        var observed = new ConcurrentQueue<(string Topic, byte[] Payload)>();
        await using var observer = await ObserveAsync(_broker.Hostname, brokerPort, observed, token);

        var line = new FormationSessions(FirstSession);
        await using var publisher = new MqttSparkplugPublisher(
            new SimulatorOptions
            {
                BrokerHost = "127.0.0.1",
                BrokerPort = link.Port,
                LinePath = LinePath,
                BirthDeathSequence = FirstSession,
                ReconnectDelay = TimeSpan.FromMilliseconds(200),
            },
            NullLogger<MqttSparkplugPublisher>.Instance);

        var reborn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.BeginSession = line.BeginSession;
        publisher.SessionRestored = async _ =>
        {
            await publisher.PublishAsync(NodeBirth(line.Current), CancellationToken.None);
            reborn.TrySetResult();
        };

        await publisher.ConnectAsync(token);
        await publisher.PublishAsync(NodeBirth(line.Current), token);

        await WaitForAsync(observed, SparkplugMessageType.NodeBirth, token);
        observed.Clear();

        // The cable comes out.
        link.Cut();

        var death = await WaitForAsync(observed, SparkplugMessageType.NodeDeath, token);
        var birth = await WaitForAsync(observed, SparkplugMessageType.NodeBirth, token);
        await reborn.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

        // The will names the session that ended, not the one that replaced it. The gateway compares
        // exactly these two numbers to decide whether a death still applies, so a will carrying the
        // NEW bdSeq would let a late delivery kill a session that is alive and publishing.
        BirthDeathSequenceOf(death).ShouldBe(FirstSession);
        BirthDeathSequenceOf(birth).ShouldBe(FirstSession + 1);
        line.Current.ShouldBe(FirstSession + 1);
    }

    private static ulong BirthDeathSequenceOf(byte[] payload)
    {
        var readings = SparkplugPayload.DecodeData(payload, MetricAliasTable.Empty);
        var metric = readings.Single(reading =>
            string.Equals(reading.MetricName, SparkplugPayload.BirthDeathSequenceMetric, StringComparison.Ordinal));

        return (ulong)((MetricValue.Integral)metric.Value).Value;
    }

    private static SparkplugMessage NodeBirth(ulong birthDeathSequence)
    {
        var path = Nvm.Kernel.Identity.EquipmentPath.Parse(LinePath);
        var payload = SparkplugPayload.EncodeBirth(
            [
                new DeviceReading(
                    SparkplugPayload.BirthDeathSequenceMetric,
                    Alias: null,
                    new MetricValue.Integral((long)birthDeathSequence),
                    DateTimeOffset.UnixEpoch),
            ],
            sequence: 0,
            DateTimeOffset.UnixEpoch);

        return new SparkplugMessage(SparkplugTopic.For(path, SparkplugMessageType.NodeBirth), [.. payload]);
    }

    private static async Task<byte[]> WaitForAsync(
        ConcurrentQueue<(string Topic, byte[] Payload)> observed,
        SparkplugMessageType messageType,
        CancellationToken cancellationToken)
    {
        var token = messageType.Token();
        var clock = TimeProvider.System;
        var deadline = clock.GetUtcNow().AddSeconds(30);

        while (clock.GetUtcNow() < deadline)
        {
            foreach (var (topic, payload) in observed)
            {
                if (topic.Contains($"/{token}/", StringComparison.Ordinal))
                {
                    return payload;
                }
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"No {token} arrived within 30 s.");
    }

    private static async Task<IAsyncDisposable> ObserveAsync(
        string host,
        int port,
        ConcurrentQueue<(string Topic, byte[] Payload)> observed,
        CancellationToken cancellationToken)
    {
        var client = new MqttClientFactory().CreateMqttClient();

        client.ApplicationMessageReceivedAsync += arguments =>
        {
            // Typed local first: ImmutableArray's ToArray extension is in scope and makes the call
            // on a ReadOnlySequence ambiguous. Same reason the gateway does this.
            ReadOnlySequence<byte> payload = arguments.ApplicationMessage.Payload;
            observed.Enqueue((arguments.ApplicationMessage.Topic, payload.ToArray()));
            return Task.CompletedTask;
        };

        await client.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId("nvm-test-observer")
                .Build(),
            cancellationToken);

        await client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter
                    .WithTopic($"{SparkplugTopic.Namespace}/#")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(),
            cancellationToken);

        return new Disposer(client);
    }

    private sealed class Disposer(IMqttClient client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Tearing down an observer must not fail a test that already has its answer.
            }

            client.Dispose();
        }
    }

    // The bdSeq counter the line owns, reduced to the part this test needs.
    private sealed class FormationSessions(ulong first)
    {
        public ulong Current { get; private set; } = first;

        public ulong BeginSession() => ++Current;
    }
}
