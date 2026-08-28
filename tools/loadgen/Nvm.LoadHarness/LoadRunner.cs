using System.Buffers;
using System.Diagnostics;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.LoadHarness;

/// <summary>Publishes Sparkplug traffic at a target rate and reports what it actually achieved.</summary>
/// <remarks>
/// <para>
/// <b>Every clock here is right.</b> D2's lag is <c>recorded_at − device_timestamp</c>, a subtraction
/// of two clocks, so a drifted device would contribute a lag measured in hours and a fast one a
/// negative lag. The harness therefore injects no clock fault at all, and that has to be written
/// next to the number in <c>benchmarks.md</c> — otherwise somebody reading the table at M8 concludes
/// the pipeline was once faster than causality.
/// </para>
/// <para>
/// The rate is enforced against a deadline that does not drift: the Nth message is due at
/// <c>start + N × interval</c>, computed from the start rather than by adding a delay after each send.
/// Re-arming after the work is done makes the real period "interval plus however long publishing
/// took", and the harness would quietly measure a slower plant than the one on paper.
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

    /// <summary>Creates a runner over the channels the plant actually has.</summary>
    /// <param name="options">Rate, duration and broker.</param>
    /// <param name="channels">Channel paths from the factory model.</param>
    /// <param name="clock">Clock stamping device timestamps and pacing the run (K1).</param>
    /// <exception cref="ArgumentException">The plant has no channels to publish for.</exception>
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

    /// <summary>Runs the whole load and returns what it achieved.</summary>
    /// <param name="cancellationToken">Stops the run early.</param>
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
            // One mark per channel, carried from the births into the data run: a DBIRTH reading and
            // the first DDATA of that channel can land in the same millisecond too.
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
                // Actual elapsed, never the requested duration. A run that took 604 seconds to send
                // ten minutes of traffic did 4.967 msg/s, and dividing by the 600 it was ASKED for
                // reports 5.000 - the harness grading itself on its intention. D2 is a measurement.
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

    // One NBIRTH and one DBIRTH per channel, before any data. Without them the gateway refuses every
    // alias-only message that follows (C02) and the run would measure the rejection path.
    private async Task<long> DeclareAsync(
        IMqttClient client,
        EquipmentPath linePath,
        SparkplugSessionSequence sequence,
        long[] lastMillisecond,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        // A repeated harness invocation is a new node session even when the gateway process stays
        // alive. Reusing bdSeq=1 made its NBIRTH look like a duplicate of the previous run, so the
        // tracker correctly kept the old LastSequence and diagnosed the new seq=0 as a gap.
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

    // Sparkplug B carries a device timestamp as uint64 MILLISECONDS, and a measurement's identity is
    // (site, equipment, unit, step, device_timestamp, signal). Two readings of one signal on one
    // device inside the same millisecond are therefore ONE measurement by definition, and ingestion
    // is right to store a single row. Counting published messages and calling the difference "loss"
    // sends somebody hunting a bug in deduplication that is not there: measured 12.833 of 542.427
    // readings (2,37%) at eight channels, because 4.794 msg/s over eight channels puts a reading on
    // each one every 1,67 ms.
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
            // Computed from the start, never accumulated. Adding a delay after each send makes the
            // real period "interval plus however long publishing took".
            var deadline = interval * due;
            var behind = _clock.GetElapsedTime(started);

            if (behind >= _options.Duration)
            {
                break;
            }

            // The window is [start, start + duration). Without this boundary check, the iteration
            // that observes 59.999 s may schedule one extra message exactly at 60.000 s. Waiting out
            // the final fraction also makes "ran for N seconds" true without counting that extra.
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
            // Tie the device clock to the same non-drifting schedule as the rate deadline. If the
            // publisher falls behind, this timestamp stays at the intended sample instant, so D2
            // includes that source-side delay instead of hiding it behind a fresh wall-clock read.
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

    // The device clock is the harness's own clock, exactly. That is the point: D2's lag has to be
    // the pipeline's, and any drift here would be measured as pipeline latency.
    private static DeviceReading Reading(DateTimeOffset now) =>
        new("Formation/Voltage", Alias: 1, FormationVoltage, now);
}

/// <summary>What a load run achieved.</summary>
/// <param name="Sent">Messages the broker accepted.</param>
/// <param name="Failed">Publishes that threw. Must be zero for D2 to mean anything.</param>
/// <param name="Measurements">
/// Distinct measurements published, births included — the number that must equal the row delta.
/// It is smaller than <paramref name="Sent"/> whenever two readings of one channel share a
/// millisecond, which is a property of Sparkplug's timestamp and not a loss.
/// </param>
/// <param name="DeclaredMessages">Birth messages sent before the data run.</param>
/// <param name="Elapsed">Wall time the run took.</param>
/// <param name="AchievedRate">Successful publishes per second in the requested measurement window.</param>
/// <param name="RebirthRequests">Node commands caused by gaps or an unreadable alias.</param>
public sealed record LoadResult(
    long Sent,
    long Failed,
    long Measurements,
    long DeclaredMessages,
    TimeSpan Elapsed,
    double AchievedRate,
    long RebirthRequests);
