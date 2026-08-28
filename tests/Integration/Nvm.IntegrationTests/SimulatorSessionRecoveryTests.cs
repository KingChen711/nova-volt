using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>
/// R6, second half. <see cref="SimulatorMqttDisconnectTests"/> proved the broker publishes the will
/// and the publisher dials back in — but it drove <see cref="MqttSparkplugPublisher"/> on its own,
/// with a stub standing in for the line and a hand-written NBIRTH standing in for a declaration.
/// Nothing in it ran the worker, so nothing in it could see the two failures that only exist when a
/// plant is producing data while the link dies underneath it.
///
/// <para>
/// This runs the real <see cref="SimulatorWorker"/> over the real line and cuts the connection while
/// <c>DDATA</c> is flowing. What has to follow is what a gateway on the other end depends on: the
/// plant keeps running, the will names the session that ended, and the session that replaces it is
/// <b>readable</b> — declared from <c>seq</c> zero, every device born before any of its data.
/// </para>
/// </summary>
public sealed class SimulatorSessionRecoveryTests : IAsyncLifetime
{
    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

    private static readonly EquipmentPath[] Channels =
    [
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001"),
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0002"),
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0003"),
    ];

    private static readonly DateTimeOffset StartedAt = new(2026, 8, 29, 7, 0, 0, TimeSpan.Zero);

    /// <summary>Sparkplug rolls <c>seq</c> here, and a consumer counts on the wrap.</summary>
    private const ulong SequenceWrap = 256;

    private const ulong FirstSession = 4;

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

    public async ValueTask DisposeAsync() => await _broker.DisposeAsync();

    [Fact]
    public async Task ACutConnection_KeepsTheLineRunningAndLeavesTheNewSessionReadable()
    {
        var token = TestContext.Current.CancellationToken;
        var brokerPort = _broker.GetMappedPublicPort(1883);

        // Only the simulator goes through the proxy. The observer talks to the broker directly, so
        // cutting the link is something that happens to the plant and not to the instrument watching
        // it — otherwise the test would lose the evidence at the moment it is produced.
        await using var link = new CuttableLink(_broker.Hostname, brokerPort);

        var observed = new ConcurrentQueue<Observation>();
        await using var observer = await ObserveAsync(_broker.Hostname, brokerPort, observed, token);

        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            BrokerHost = "127.0.0.1",
            BrokerPort = link.Port,
            BirthDeathSequence = FirstSession,
            SamplePeriod = TimeSpan.FromMinutes(1),

            // 100 ms per tick, against a reconnect that takes at least 750. The window has to be
            // several ticks wide or the test would only sometimes publish inside it, and a test that
            // only sometimes reproduces a defect is a test that will one day be deleted as flaky.
            TimeCompression = 600,
            ReconnectDelay = TimeSpan.FromMilliseconds(750),
            ReportInterval = TimeSpan.FromSeconds(1),
            ReportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json"),
        };

        var line = new FormationLine(LinePath, Channels, FormationProfile.Default, StartedAt, FirstSession);

        await using var mqtt = new MqttSparkplugPublisher(options, NullLogger<MqttSparkplugPublisher>.Instance);

        // Through the fault injector, because that is the arrangement the simulator actually runs in.
        // Every rate is zero here: the only thing allowed to go wrong in this test is the cable.
        var publisher = new FaultInjectingPublisher(
            mqtt,
            options.Faults,
            TimeProvider.System,
            NullLogger<FaultInjectingPublisher>.Instance);

        using var worker = new SimulatorWorker(
            line, publisher, options, TimeProvider.System, NullLogger<SimulatorWorker>.Instance);

        Observation[] messages;

        await worker.StartAsync(token);

        try
        {
            await WaitUntilAsync(
                () => Count([.. observed], SparkplugMessageType.DeviceData) >= 8,
                "The line never produced data before the cut, so there was nothing to interrupt.",
                token);

            var beforeCut = line.MeasurementCount;

            // The cable comes out.
            link.Cut();

            await WaitUntilAsync(
                () => Reborn([.. observed]) >= 0,
                $"The node never came back under bdSeq {FirstSession + 1}.",
                token);

            await WaitUntilAsync(
                () =>
                {
                    Observation[] snapshot = [.. observed];
                    var reborn = Reborn(snapshot);

                    return reborn >= 0 && Count(snapshot[reborn..], SparkplugMessageType.DeviceData) >= 4;
                },
                "The node re-declared itself and then went quiet, so the line did not survive the cut.",
                token);

            messages = [.. observed];

            // 1. The plant did not stop. A publish inside the reconnect window used to throw
            //    MqttClientNotConnectedException straight out of ExecuteAsync, which ends the
            //    BackgroundService and, in the real host, the process — a cut cable taking the line
            //    down with it, which is exactly what N15 forbids.
            var execute = worker.ExecuteTask
                ?? throw new InvalidOperationException("The worker never started, so nothing was cut.");

            execute.IsCompleted.ShouldBeFalse(
                "The worker stopped when the connection was cut. " + FaultText(execute));
            worker.IsRunning.ShouldBeTrue();
            line.MeasurementCount.ShouldBeGreaterThan(beforeCut, "The line took no readings after the cut.");

            // Nobody asked for a rebirth in this test, so every seq reset below is a session
            // boundary and not a re-declaration inside one.
            worker.Rebirths.ShouldBe(0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(options.ReportPath);
        }

        // 2. The will names the session that ENDED. A death carrying the new number would let a late
        //    delivery mark a node stale that is alive and publishing.
        var deaths = messages.Where(m => m.Topic.MessageType == SparkplugMessageType.NodeDeath).ToArray();
        deaths.Length.ShouldBe(1, "One cut cable is one death.");
        SparkplugPayload.DecodeDeath(deaths[0].Payload).BirthDeathSequence.ShouldBe(FirstSession);

        var reborn = Reborn(messages);
        var died = Array.FindIndex(messages, m => m.Topic.MessageType == SparkplugMessageType.NodeDeath);

        // Nothing from the dead session got out on the new connection. The window between the will
        // and the new birth belongs to the reconnect, and a DDATA in it is a message the old session
        // numbered being published after that session ended - one stale seq sitting in front of the
        // declaration, where a consumer counting seq cannot make sense of it.
        messages[died..reborn]
            .ShouldAllBe(m => m.Topic.MessageType != SparkplugMessageType.DeviceData);

        var tail = messages[reborn..];

        // 3. The new session declares itself completely before it says anything else: NBIRTH, then a
        //    DBIRTH for every channel, and no data wedged in among them.
        var declaration = tail[..(1 + Channels.Length)];
        declaration[0].Topic.MessageType.ShouldBe(SparkplugMessageType.NodeBirth);
        declaration[1..].ShouldAllBe(m => m.Topic.MessageType == SparkplugMessageType.DeviceBirth);
        declaration[1..].Select(m => m.Topic.DeviceCode!).Order(StringComparer.Ordinal)
            .ShouldBe(Channels.Select(channel => channel.Code).Order(StringComparer.Ordinal));

        // 4 and 5. Read the tail the way the gateway reads it. Two properties fall out of one walk,
        //    and neither can be checked from inside the simulator:
        //
        //      seq is contiguous from zero — so nothing composed under the dead session slipped into
        //      the new one, and Advance never took a number out of the middle of Connect's run. That
        //      interleaving is what evaluating _line.Advance(...) outside the publishing lock made
        //      possible, and a consumer meets it as a gap it never missed anything over.
        //
        //      every DDATA decodes — against the alias table THIS session's DBIRTH declared. A DDATA
        //      published before its birth throws UnknownMetricAliasException right here, which is
        //      the same wall a real gateway hits and the reason the ordering matters at all.
        var aliases = new Dictionary<string, MetricAliasTable>(StringComparer.Ordinal);
        var expected = 0UL;
        var readings = 0;

        foreach (var (topic, payload) in tail)
        {
            ulong? sequence;

            switch (topic.MessageType)
            {
                case SparkplugMessageType.NodeBirth:
                    sequence = SparkplugPayload.DecodeBirth(payload).Sequence;
                    break;

                case SparkplugMessageType.DeviceBirth:
                    var birth = SparkplugPayload.DecodeBirth(payload);
                    aliases[topic.DeviceCode!] = birth.Aliases;
                    sequence = birth.Sequence;
                    break;

                case SparkplugMessageType.DeviceData:
                    aliases.ShouldContainKey(
                        topic.DeviceCode!,
                        $"'{topic.Value}' arrived before the DBIRTH that names its aliases.");
                    readings += SparkplugPayload
                        .DecodeData(payload, aliases[topic.DeviceCode!], out sequence)
                        .Length;
                    break;

                default:
                    continue;
            }

            sequence.ShouldBe(expected, $"'{topic.Value}' broke the sequence of the new session.");
            expected = (expected + 1) % SequenceWrap;
        }

        readings.ShouldBeGreaterThan(0, "The new session declared itself and then measured nothing.");
    }

    // Where the second session starts, or -1 when it has not started yet. The bdSeq is what says so:
    // the node publishes an NBIRTH for the first session too, and the two are only told apart by the
    // number they carry.
    private static int Reborn(IReadOnlyList<Observation> messages)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].Topic.MessageType == SparkplugMessageType.NodeBirth
                && SparkplugPayload.DecodeBirth(messages[index].Payload).BirthDeathSequence == FirstSession + 1)
            {
                return index;
            }
        }

        return -1;
    }

    private static int Count(IEnumerable<Observation> messages, SparkplugMessageType messageType) =>
        messages.Count(message => message.Topic.MessageType == messageType);

    private static string FaultText(Task? task) =>
        task?.Exception is { } fault ? fault.ToString() : "It completed without an exception.";

    private static async Task WaitUntilAsync(Func<bool> condition, string whatWentWrong, CancellationToken cancellationToken)
    {
        var clock = TimeProvider.System;
        var deadline = clock.GetUtcNow().AddSeconds(30);

        while (clock.GetUtcNow() < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException(whatWentWrong);
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

    private static async Task<IAsyncDisposable> ObserveAsync(
        string host,
        int port,
        ConcurrentQueue<Observation> observed,
        CancellationToken cancellationToken)
    {
        var client = new MqttClientFactory().CreateMqttClient();

        client.ApplicationMessageReceivedAsync += arguments =>
        {
            // Typed local first: ImmutableArray's ToArray extension is in scope and makes the call
            // on a ReadOnlySequence ambiguous. Same reason the gateway does this.
            ReadOnlySequence<byte> payload = arguments.ApplicationMessage.Payload;

            if (SparkplugTopic.TryParse(arguments.ApplicationMessage.Topic, out var topic))
            {
                observed.Enqueue(new Observation(topic, payload.ToArray()));
            }

            return Task.CompletedTask;
        };

        await client.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId("nvm-test-session-observer")
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

    /// <summary>One message as it reached a consumer, in arrival order.</summary>
    private sealed record Observation(SparkplugTopic Topic, byte[] Payload);

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
}
