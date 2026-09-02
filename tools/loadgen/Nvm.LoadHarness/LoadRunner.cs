using System.Buffers;
using System.Diagnostics;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.LoadHarness;

/// <summary>Publish traffic Sparkplug ở một target rate và báo cáo những gì nó thực sự đạt được.</summary>
/// <remarks>
/// <para>
/// <b>Mọi đồng hồ ở đây đều đúng.</b> Lag của D2 là <c>recorded_at − device_timestamp</c>, một phép
/// trừ giữa hai đồng hồ, nên một device bị trôi sẽ đóng góp một lag đo bằng giờ và một device nhanh
/// sẽ cho một lag âm. Vì vậy harness không tiêm bất kỳ lỗi đồng hồ nào cả, và điều đó phải được viết
/// ra bên cạnh con số trong <c>benchmarks.md</c> — nếu không ai đó đọc bảng đó ở M8 sẽ kết luận rằng
/// pipeline từng nhanh hơn cả quan hệ nhân quả.
/// </para>
/// <para>
/// Rate được thực thi dựa trên một deadline không bị trôi: message thứ N đến hạn ở
/// <c>start + N × interval</c>, tính từ điểm bắt đầu thay vì bằng cách cộng thêm delay sau mỗi lần
/// gửi. Nạp lại sau khi công việc xong sẽ khiến chu kỳ thực tế trở thành "interval cộng với thời gian
/// publish mất bao lâu", và harness sẽ âm thầm đo một plant chậm hơn cái ghi trên giấy.
/// </para>
/// </remarks>
public sealed class LoadRunner
{
    private const string RebirthControlMetric = "Node Control/Rebirth";
    private static readonly MetricValue.Real FormationVoltage = new(3.7);

    private readonly LoadHarnessOptions _options;
    private readonly IReadOnlyList<EquipmentPath> _channels;
    private readonly string[] _dataTopics;
    private readonly TimeProvider _clock;

    /// <summary>Tạo một runner dựa trên các channel mà plant thực sự có.</summary>
    /// <param name="options">Rate, duration và broker.</param>
    /// <param name="channels">Các channel path từ factory model.</param>
    /// <param name="clock">Đồng hồ đóng dấu device timestamp và điều tiết nhịp độ của run (K1).</param>
    /// <exception cref="ArgumentException">Plant không có channel nào để publish.</exception>
    public LoadRunner(LoadHarnessOptions options, IReadOnlyList<EquipmentPath> channels, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(clock);

        if (channels.Count == 0)
        {
            throw new ArgumentException("The harness needs at least one channel to publish for.", nameof(channels));
        }

        _options = options;
        _channels = channels;
        _dataTopics = [.. channels.Select(channel => SparkplugTopic.For(channel, SparkplugMessageType.DeviceData).Value)];
        _clock = clock;
    }

    /// <summary>Chạy toàn bộ load và trả về những gì nó đạt được.</summary>
    /// <param name="cancellationToken">Dừng run sớm.</param>
    public async Task<LoadResult> RunAsync(CancellationToken cancellationToken)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var linePath = EquipmentPath.Parse(_options.LinePath);
        var commandTopic = SparkplugTopic.For(linePath, SparkplugMessageType.NodeCommand).Value;
        long rebirthRequests = 0;

        client.ApplicationMessageReceivedAsync += arguments =>
        {
            if (IsRebirthRequest(arguments, commandTopic))
            {
                Interlocked.Increment(ref rebirthRequests);
            }

            return Task.CompletedTask;
        };

        try
        {
            await client.ConnectAsync(
                new MqttClientOptionsBuilder()
                    .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
                    .WithClientId($"nvm-load-EDGE-{linePath.Code}")
                    .WithCleanSession()
                    .Build(),
                cancellationToken);

            await client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(filter => filter
                        .WithTopic(commandTopic)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build(),
                cancellationToken);

            var sequence = new SparkplugSessionSequence();
            // Một dấu mốc cho mỗi channel, mang theo từ các birth vào data run: một DBIRTH reading và
            // DDATA đầu tiên của channel đó cũng có thể rơi vào cùng một mili-giây.
            var lastMillisecond = new long[_channels.Count];
            Array.Fill(lastMillisecond, long.MinValue);

            var declared = await DeclareAsync(client, linePath, sequence, lastMillisecond, cancellationToken);

            var started = Stopwatch.GetTimestamp();
            var counts = await PublishDataAsync(client, sequence, lastMillisecond, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);
            await Task.Delay(TimeSpan.FromSeconds(2), _clock, cancellationToken);

            return new LoadResult(
                counts.Sent,
                counts.Failed,
                declared + counts.Distinct,
                _channels.Count,
                elapsed,
                // Elapsed thực tế, không bao giờ là duration được yêu cầu. Một run tốn 604 giây để gửi
                // mười phút traffic đã đạt 4.967 msg/s, và chia cho 600 giây mà nó ĐƯỢC YÊU CẦU sẽ báo
                // cáo 5.000 - harness tự chấm điểm mình dựa trên ý định của nó. D2 là một phép đo.
                counts.Sent / elapsed.TotalSeconds,
                Interlocked.Read(ref rebirthRequests));
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(cancellationToken: CancellationToken.None);
            }

            client.Dispose();
        }
    }

    // Một NBIRTH và một DBIRTH cho mỗi channel, trước bất kỳ data nào. Không có chúng, gateway sẽ từ
    // chối mọi message chỉ-mang-alias theo sau (C02) và run sẽ đo phải con đường bị từ chối.
    private async Task<long> DeclareAsync(
        IMqttClient client,
        EquipmentPath linePath,
        SparkplugSessionSequence sequence,
        long[] lastMillisecond,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        // Một lần gọi lặp lại harness là một node session mới ngay cả khi gateway process vẫn sống.
        // Dùng lại bdSeq=1 khiến NBIRTH của nó trông giống một bản trùng của run trước, nên tracker đã
        // đúng đắn giữ nguyên LastSequence cũ và chẩn đoán seq=0 mới là một khoảng hở.
        var birthDeathSequence = now.ToUnixTimeMilliseconds();

        await PublishAsync(
            client,
            SparkplugTopic.For(linePath, SparkplugMessageType.NodeBirth),
            SparkplugPayload.EncodeBirth(
                [new DeviceReading(
                    SparkplugPayload.BirthDeathSequenceMetric,
                    null,
                    new MetricValue.Integral(birthDeathSequence),
                    now)],
                sequence.TakeNext(),
                now),
            cancellationToken);

        long distinct = 0;

        for (var index = 0; index < _channels.Count; index++)
        {
            await PublishAsync(
                client,
                SparkplugTopic.For(_channels[index], SparkplugMessageType.DeviceBirth),
                SparkplugPayload.EncodeBirth([Reading(now)], sequence.TakeNext(), now),
                cancellationToken);

            if (IsNewMeasurement(lastMillisecond, index, now))
            {
                distinct++;
            }
        }

        return distinct;
    }

    // Sparkplug B mang device timestamp dưới dạng uint64 MILI-GIÂY, và identity của một phép đo là
    // (site, equipment, unit, step, device_timestamp, signal). Vì vậy hai reading của một signal trên
    // một device trong cùng một mili-giây là MỘT phép đo theo định nghĩa, và ingestion lưu một row
    // duy nhất là đúng. Đếm message đã publish rồi gọi phần chênh lệch là "mất mát" sẽ khiến ai đó đi
    // săn một bug deduplication không hề tồn tại: đo được 12.833 trên 542.427 reading (2,37%) ở tám
    // channel, vì 4.794 msg/s trên tám channel đặt một reading lên mỗi channel mỗi 1,67 ms.
    private static bool IsNewMeasurement(long[] lastMillisecond, int channel, DateTimeOffset at)
    {
        var milliseconds = at.ToUnixTimeMilliseconds();

        if (lastMillisecond[channel] == milliseconds)
        {
            return false;
        }

        lastMillisecond[channel] = milliseconds;
        return true;
    }

    private async Task<(long Sent, long Failed, long Distinct)> PublishDataAsync(
        IMqttClient client,
        SparkplugSessionSequence sequence,
        long[] lastMillisecond,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1 / (double)_options.Rate);
        var started = _clock.GetTimestamp();
        var deviceStartedAt = _clock.GetUtcNow();
        var inFlight = new Queue<Task<bool>>(_options.MaxInFlightPublishes);
        long sent = 0;
        long failed = 0;
        long distinct = 0;
        long due = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Tính từ điểm bắt đầu, không bao giờ cộng dồn. Thêm delay sau mỗi lần gửi sẽ khiến chu kỳ
            // thực tế trở thành "interval cộng với thời gian publish mất bao lâu".
            var deadline = interval * due;
            var behind = _clock.GetElapsedTime(started);

            if (behind >= _options.Duration)
            {
                break;
            }

            // Window là [start, start + duration). Không có kiểm tra biên này, lần lặp quan sát thấy
            // 59.999 s có thể lên lịch thêm một message ngay đúng 60.000 s. Chờ hết phần lẻ cuối cùng
            // cũng khiến "chạy trong N giây" đúng là sự thật mà không tính thêm message dư đó.
            if (deadline >= _options.Duration)
            {
                var remaining = _options.Duration - behind;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _clock, cancellationToken);
                }

                break;
            }

            if (behind < deadline)
            {
                await Task.Delay(deadline - behind, _clock, cancellationToken);
            }

            var index = (int)(due % _channels.Count);
            // Gắn device clock vào đúng lịch không trôi giống như deadline của rate. Nếu publisher bị
            // tụt lại phía sau, timestamp này vẫn giữ nguyên ở thời điểm sample dự kiến, nên D2 tính
            // cả độ trễ phía nguồn đó thay vì che giấu nó sau một lần đọc wall-clock mới.
            var now = deviceStartedAt + deadline;

            if (IsNewMeasurement(lastMillisecond, index, now))
            {
                distinct++;
            }

            inFlight.Enqueue(PublishOneAsync(client, _dataTopics[index], sequence.TakeNext(), now, cancellationToken));

            if (inFlight.Count >= _options.MaxInFlightPublishes)
            {
                Count(await inFlight.Dequeue());
            }

            due++;
        }

        while (inFlight.TryDequeue(out var publish))
        {
            Count(await publish);
        }

        return (sent, failed, distinct);

        void Count(bool succeeded)
        {
            if (succeeded)
            {
                sent++;
            }
            else
            {
                failed++;
            }
        }
    }

    private static async Task<bool> PublishOneAsync(
        IMqttClient client,
        string topic,
        ulong sequence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await PublishAsync(
                client,
                topic,
                SparkplugPayload.EncodeData([Reading(now)], sequence, now),
                cancellationToken);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static Task<MqttClientPublishResult> PublishAsync(
        IMqttClient client,
        SparkplugTopic topic,
        byte[] payload,
        CancellationToken cancellationToken) => PublishAsync(client, topic.Value, payload, cancellationToken);

    private static Task<MqttClientPublishResult> PublishAsync(
        IMqttClient client,
        string topic,
        byte[] payload,
        CancellationToken cancellationToken) => client.PublishAsync(
        new MqttApplicationMessage
        {
            Topic = topic,
            PayloadSegment = payload,
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce,
        },
        cancellationToken);

    private static bool IsRebirthRequest(
        MqttApplicationMessageReceivedEventArgs arguments,
        string commandTopic)
    {
        if (!string.Equals(arguments.ApplicationMessage.Topic, commandTopic, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySequence<byte> payload = arguments.ApplicationMessage.Payload;
        var readings = SparkplugPayload.DecodeData(payload.ToArray(), MetricAliasTable.Empty);

        return readings.Any(reading =>
            string.Equals(reading.MetricName, RebirthControlMetric, StringComparison.Ordinal)
            && reading.Value is MetricValue.Flag { Value: true });
    }

    // Đồng hồ device chính là đồng hồ của bản thân harness, y hệt. Đó chính là điểm mấu chốt: lag của
    // D2 phải là của pipeline, và bất kỳ độ trôi nào ở đây sẽ bị đo nhầm thành pipeline latency.
    private static DeviceReading Reading(DateTimeOffset now) =>
        new("Formation/Voltage", Alias: 1, FormationVoltage, now);
}

/// <summary>Những gì một load run đã đạt được.</summary>
/// <param name="Sent">Message mà broker đã chấp nhận.</param>
/// <param name="Failed">Các publish đã throw. Phải bằng 0 thì D2 mới có ý nghĩa.</param>
/// <param name="Measurements">
/// Số phép đo riêng biệt đã publish, tính cả birth — con số phải bằng đúng row delta.
/// Nó nhỏ hơn <paramref name="Sent"/> bất cứ khi nào hai reading của một channel chia sẻ chung một
/// mili-giây, đó là một tính chất của timestamp Sparkplug chứ không phải mất mát.
/// </param>
/// <param name="DeclaredMessages">Các birth message đã gửi trước khi data run bắt đầu.</param>
/// <param name="Elapsed">Wall time mà run tốn.</param>
/// <param name="AchievedRate">Số publish thành công mỗi giây trong measurement window được yêu cầu.</param>
/// <param name="RebirthRequests">Node command gây ra bởi các khoảng hở hoặc một alias không đọc được.</param>
public sealed record LoadResult(
    long Sent,
    long Failed,
    long Measurements,
    long DeclaredMessages,
    TimeSpan Elapsed,
    double AchievedRate,
    long RebirthRequests);
