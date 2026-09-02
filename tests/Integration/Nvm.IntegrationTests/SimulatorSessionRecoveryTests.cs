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
/// R6, nửa sau. <see cref="SimulatorMqttDisconnectTests"/> chứng minh broker publish will và publisher
/// kết nối lại — nhưng nó chạy riêng <see cref="MqttSparkplugPublisher"/>, với stub thay cho line và
/// NBIRTH viết tay thay cho declaration. Nó không chạy worker, nên không thấy hai failure chỉ tồn tại
/// khi nhà máy đang tạo data mà link chết bên dưới.
///
/// <para>
/// Test này chạy <see cref="SimulatorWorker"/> thật trên line thật và cắt connection khi <c>DDATA</c>
/// đang chảy. Điều phải theo sau là thứ gateway ở đầu kia phụ thuộc vào: nhà máy vẫn chạy, will gọi tên
/// session đã kết thúc, và session thay thế <b>đọc được</b> — declaration từ <c>seq</c> zero, mọi
/// device birth trước bất kỳ data nào của nó.
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

    /// <summary>Sparkplug roll <c>seq</c> ở đây, và consumer dựa vào việc wrap.</summary>
    private const ulong SequenceWrap = 256;

    private const ulong FirstSession = 4;

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

    public async ValueTask DisposeAsync() => await _broker.DisposeAsync();

    [Fact]
    public async Task ACutConnection_KeepsTheLineRunningAndLeavesTheNewSessionReadable()
    {
        var token = TestContext.Current.CancellationToken;
        var brokerPort = _broker.GetMappedPublicPort(1883);

        // Chỉ simulator đi qua proxy. Observer nói với broker trực tiếp, nên cắt link là việc xảy ra
        // với nhà máy chứ không với instrument quan sát nó — nếu không test mất evidence ngay lúc nó sinh ra.
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

            // 100 ms mỗi tick, so với reconnect mất tối thiểu 750. Window phải rộng vài tick, nếu không
            // test chỉ đôi khi publish trong nó; test chỉ đôi khi reproduce defect thì sẽ bị xóa vì flaky.
            TimeCompression = 600,
            ReconnectDelay = TimeSpan.FromMilliseconds(750),
            ReportInterval = TimeSpan.FromSeconds(1),
            ReportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json"),
        };

        var line = new FormationLine(LinePath, Channels, FormationProfile.Default, StartedAt, FirstSession);

        await using var mqtt = new MqttSparkplugPublisher(options, NullLogger<MqttSparkplugPublisher>.Instance);

        // Qua fault injector vì đó là arrangement mà simulator thực sự chạy. Mọi rate ở đây bằng zero:
        // điều duy nhất được phép sai trong test là cáp.
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

            // Cáp bị rút.
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

            // 1. Nhà máy không dừng. Publish trong reconnect window từng ném
            //    MqttClientNotConnectedException thẳng từ ExecuteAsync, làm kết thúc BackgroundService
            //    và trong host thật, cả process — cáp bị rút kéo line xuống, đúng điều N15 cấm.
            var execute = worker.ExecuteTask
                ?? throw new InvalidOperationException("The worker never started, so nothing was cut.");

            execute.IsCompleted.ShouldBeFalse(
                "The worker stopped when the connection was cut. " + FaultText(execute));
            worker.IsRunning.ShouldBeTrue();
            line.MeasurementCount.ShouldBeGreaterThan(beforeCut, "The line took no readings after the cut.");

            // Không ai yêu cầu rebirth trong test này, nên mọi lần reset seq dưới đây là session
            // boundary chứ không phải re-declaration trong một session.
            worker.Rebirths.ShouldBe(0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(options.ReportPath);
        }

        // 2. Will gọi tên session ĐÃ KẾT THÚC. Death mang số mới sẽ để delivery muộn đánh dấu stale
        //    một node còn sống và đang publish.
        var deaths = messages.Where(m => m.Topic.MessageType == SparkplugMessageType.NodeDeath).ToArray();
        deaths.Length.ShouldBe(1, "One cut cable is one death.");
        SparkplugPayload.DecodeDeath(deaths[0].Payload).BirthDeathSequence.ShouldBe(FirstSession);

        var reborn = Reborn(messages);
        var died = Array.FindIndex(messages, m => m.Topic.MessageType == SparkplugMessageType.NodeDeath);

        // Không gì từ session chết ra được connection mới. Window giữa will và birth mới thuộc reconnect,
        // còn DDATA trong đó là message session cũ đã đánh số nhưng publish sau khi session ấy kết thúc —
        // một seq stale đứng trước declaration, nơi consumer đếm seq không thể hiểu được.
        messages[died..reborn]
            .ShouldAllBe(m => m.Topic.MessageType != SparkplugMessageType.DeviceData);

        var tail = messages[reborn..];

        // 3. Session mới declaration đầy đủ trước khi nói điều khác: NBIRTH, rồi DBIRTH cho mọi channel,
        //    và không data nào chen giữa chúng.
        var declaration = tail[..(1 + Channels.Length)];
        declaration[0].Topic.MessageType.ShouldBe(SparkplugMessageType.NodeBirth);
        declaration[1..].ShouldAllBe(m => m.Topic.MessageType == SparkplugMessageType.DeviceBirth);
        declaration[1..].Select(m => m.Topic.DeviceCode!).Order(StringComparer.Ordinal)
            .ShouldBe(Channels.Select(channel => channel.Code).Order(StringComparer.Ordinal));

        // 4 và 5. Đọc tail như gateway đọc. Hai property rút ra từ một walk, và không cái nào check được
        //    từ bên trong simulator:
        //
        //      seq contiguous từ zero — nên không thứ nào compose dưới session chết lọt vào session mới,
        //      và Advance không lấy số từ giữa run của Connect. Interleaving này là điều evaluate
        //      _line.Advance(...) ngoài publishing lock từng cho phép; consumer gặp nó như gap dù không
        //      bỏ lỡ gì.
        //
        //      mọi DDATA decode được — theo alias table mà DBIRTH của session NÀY declaration. DDATA
        //      publish trước birth ném UnknownMetricAliasException ngay đây, cùng bức tường gateway
        //      thật đụng phải và là lý do thứ tự quan trọng.
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

    // Nơi session thứ hai bắt đầu, hoặc -1 nếu chưa bắt đầu. bdSeq nói điều đó: node cũng publish NBIRTH
    // cho session đầu, và chỉ số mà chúng mang mới phân biệt được hai session.
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

    private static async Task<IAsyncDisposable> ObserveAsync(
        string host,
        int port,
        ConcurrentQueue<Observation> observed,
        CancellationToken cancellationToken)
    {
        var client = new MqttClientFactory().CreateMqttClient();

        client.ApplicationMessageReceivedAsync += arguments =>
        {
            // Khai báo local có type trước: extension ToArray của ImmutableArray trong scope làm lời gọi
            // trên ReadOnlySequence ambiguous. Cùng lý do gateway làm vậy.
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

    /// <summary>Một message khi nó đến consumer, theo thứ tự arrival.</summary>
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
                // Tear down observer không được làm fail test đã có đáp án.
            }

            client.Dispose();
        }
    }
}
