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
/// R6. Dropout fault sẵn có của simulator giữ message trong RAM rồi thả ra sau — hữu ích, nhưng nó
/// không bao giờ đóng socket, nên không thể cho thấy behavior chỉ tồn tại vì link có thể chết: broker
/// nói thay node không còn tự nói được.
///
/// Test này chạy <see cref="MqttSparkplugPublisher"/> thật với broker thật và cắt connection bên dưới,
/// như rút cáp. Hai việc phải theo sau mà unit test không thấy được: broker publish last will thành
/// <c>NDEATH</c>, và node trở lại với <c>NBIRTH</c> mới dưới <c>bdSeq</c> mới.
/// </summary>
public sealed class SimulatorMqttDisconnectTests : IAsyncLifetime
{
    private const string LinePath = "NOVAVOLT/NV1/FORMATION/F1";
    private const ulong FirstSession = 7;

    private readonly IContainer _broker = new ContainerBuilder("eclipse-mosquitto:2.0.22")
        // Image mang config này để broker khởi động được mà không cần password file.
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(1883, true)
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _broker.StartAsync(TestContext.Current.CancellationToken);
        await WaitForBrokerAsync(TestContext.Current.CancellationToken);
    }

    // Mosquitto lắng nghe trong một giây, nhưng "container đã chạy" và "port trả lời" không là cùng
    // một event; connect vào khoảng hở sẽ làm test fail vì lý do sai.
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

        // Publisher đến broker qua proxy ta kiểm soát. Giết socket của proxy là cách duy nhất tạo ra
        // điều cáp bị cắt tạo ra: không có DISCONNECT packet. Yêu cầu MQTTnet disconnect là lời tạm
        // biệt sạch, và lời tạm biệt sạch bảo broker DISCARD will — đúng case test này loại trừ.
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

        // Cáp bị rút.
        link.Cut();

        var death = await WaitForAsync(observed, SparkplugMessageType.NodeDeath, token);
        var birth = await WaitForAsync(observed, SparkplugMessageType.NodeBirth, token);
        await reborn.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

        // Will gọi tên session đã kết thúc, không phải session thay thế. Gateway compare đúng hai số
        // này để quyết định death còn áp dụng không; will mang bdSeq MỚI sẽ để delivery muộn giết
        // session còn sống và đang publish.
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
            // Khai báo local có type trước: extension ToArray của ImmutableArray trong scope làm lời gọi
            // trên ReadOnlySequence ambiguous. Cùng lý do gateway làm vậy.
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
                // Tear down observer không được làm fail test đã có đáp án.
            }

            client.Dispose();
        }
    }

    // Counter bdSeq mà line sở hữu, rút gọn còn phần test này cần.
    private sealed class FormationSessions(ulong first)
    {
        public ulong Current { get; private set; } = first;

        public ulong BeginSession() => ++Current;
    }
}
