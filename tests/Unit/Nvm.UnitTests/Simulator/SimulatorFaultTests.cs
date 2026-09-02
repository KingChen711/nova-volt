using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Reporting;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Simulator;

/// <summary>Ba cách mà nhà máy này cố ý gây lỗi, và mỗi cách không được phép làm thay đổi điều gì.</summary>
/// <remarks>
/// M2 tồn tại để sống sót qua thiết bị gửi dữ liệu tệ. Một simulator chỉ gửi tốt sẽ chứng minh rằng
/// happy path hoạt động, điều mà chẳng ai nghi ngờ cả — nên các fault này chính là input mà toàn bộ
/// milestone được đo lường dựa vào, và làm sai một cách tinh vi bất kỳ cái nào trong số đó sẽ khiến
/// mọi con số về sau tự khớp với nhau trong khi chẳng đo được gì cả.
/// </remarks>
public sealed class SimulatorFaultTests
{
    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

    private static readonly ImmutableArray<EquipmentPath> Channels =
    [
        .. Enumerable.Range(1, 8).Select(number =>
            EquipmentPath.Parse($"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-{number:0000}")),
    ];

    private static readonly DateTimeOffset StartedAt = new(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan SamplePeriod = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task OneMessageInTenIsSentASecondTime()
    {
        var messages = LogicalMessages(10_000);
        var run = await PublishAllAsync(messages, new SimulatorFaults { DuplicateRate = 0.10 });

        // Khoảng một phần mười, không phải chính xác: fault này tung xúc xắc, và một tỷ lệ ra đúng
        // tuyệt đối mỗi lần sẽ là một lịch trình chứ không phải một fault.
        run.Publisher.DuplicateMessages.ShouldBeInRange(900, 1_100);

        // Phía logic không bị chạm tới. Mười nghìn measurement đã được đưa ra và mười nghìn đã được
        // đưa ra — traffic thừa ra là do đường truyền tự lặp lại chính nó, không phải nhà máy đo
        // nhiều hơn.
        run.Recorder.Count.ShouldBe(10_000 + (int)run.Publisher.DuplicateMessages);
        run.Publisher.PublishedMessages.ShouldBe(run.Recorder.Count);

        // Mỗi message thừa ra chính là message trước nó, từng byte một. Đây là assertion quyết định
        // liệu fault này có thực sự là một bản trùng lặp hay không: một message được dựng mới từ cùng
        // các reading sẽ mang một seq mới và một device_timestamp mới, deduplication khi đó đúng khi
        // giữ lại nó, và D1 sẽ ra kết quả khớp trong khi thực ra chẳng deduplicate được gì cả (R-M2-1).
        var repeats = run.Recorder.Messages
            .Zip(run.Recorder.Messages.Skip(1))
            .Count(pair => pair.First == pair.Second);

        repeats.ShouldBe((int)run.Publisher.DuplicateMessages);
    }

    [Fact]
    public async Task ADuplicateCarriesTheSameSourceEventIdAsTheOriginal()
    {
        var line = NewLine();
        var births = line.Connect(TimeSpan.Zero);
        var aliases = AliasesByDevice(births);

        var run = await PublishAllAsync(
            Advance(line, 400),
            new SimulatorFaults { DuplicateRate = 0.10 });

        var received = run.Recorder.Messages.ToArray();

        var repeated = Enumerable.Range(1, received.Length - 1)
            .First(index =>
                received[index] == received[index - 1]
                && received[index].Topic.MessageType == SparkplugMessageType.DeviceData);

        var original = received[repeated - 1];
        var again = received[repeated];

        var path = Channels.Single(channel => channel.Code == original.Topic.DeviceCode);
        var table = aliases[original.Topic.DeviceCode!];

        // Được decode độc lập, đúng cách ingestion sẽ decode chúng — một lần ngay bây giờ và một lần
        // sau khi gateway đã giữ nó lại suốt ba giờ. Cùng một identity ở cả hai lần chính là điều
        // khiến ON CONFLICT DO NOTHING ở C12 gộp chúng thành một dòng duy nhất.
        var first = Identities(SparkplugPayload.DecodeData(original.Payload.AsSpan(), table), path);
        var second = Identities(SparkplugPayload.DecodeData(again.Payload.AsSpan(), table), path);

        first.ShouldNotBeEmpty();
        second.ShouldBe(first);

        // Và điều đó không phải là vô nghĩa: cùng một tín hiệu trên cùng một channel được đọc muộn hơn
        // một giây sẽ nhận một identity khác. Phép bằng ở trên thực sự nói lên "cùng một measurement",
        // không phải "key bỏ qua thời gian" — đó là sai lầm mà lab C13.2 tồn tại để định giá.
        var readings = SparkplugPayload.DecodeData(original.Payload.AsSpan(), table);
        var later = readings[0] with { DeviceTimestamp = readings[0].DeviceTimestamp.AddSeconds(1) };

        later.NaturalKey(path).SourceEventId.Value.ShouldNotBe(first[0]);
    }

    [Fact]
    public void AWrongClockMovesTheTimestampAndNothingElse()
    {
        var straight = NewLine();
        var drifted = NewLine(new DeviceClockDrift(1.0, TimeSpan.FromHours(2)));

        var right = straight.Connect(TimeSpan.Zero);
        var wrong = drifted.Connect(TimeSpan.Zero);

        drifted.DriftedDeviceCount.ShouldBe(Channels.Length);

        // Node birth không bị chạm tới. Drift là mặt điều khiển của thiết bị; edge node là một hộp
        // khác, và đóng dấu bdSeq bằng sai số của thiết bị sẽ khiến việc khớp session ở C11 bị lệch
        // vì một fault chưa từng chạm tới node.
        wrong[0].ShouldBe(right[0]);

        for (var index = 1; index < right.Length; index++)
        {
            var correct = SparkplugPayload.DecodeBirth(right[index].Payload.AsSpan()).Readings;
            var late = SparkplugPayload.DecodeBirth(wrong[index].Payload.AsSpan()).Readings;

            // Cùng metric, cùng giá trị, cùng cell serial. Một cục pin CMOS chết không khắc lại serial
            // của cell đang nằm trong channel, và một fault làm thay đổi cả serial sẽ là đang mô
            // phỏng một cell bị dán nhãn sai — một loại lỗi khác hẳn, và ồn ào hơn nhiều.
            late.Select(reading => reading.MetricName).ShouldBe(correct.Select(reading => reading.MetricName));
            late.Select(reading => reading.Value).ShouldBe(correct.Select(reading => reading.Value));

            late.Select(reading => reading.DeviceTimestamp)
                .Zip(correct.Select(reading => reading.DeviceTimestamp))
                .ShouldAllBe(pair => (pair.First - pair.Second).Duration() == TimeSpan.FromHours(2));
        }
    }

    [Fact]
    public void TheSameDevicesAreWrongOnEveryRunAndTheyLeanBothWays()
    {
        var drift = new DeviceClockDrift(0.10, TimeSpan.FromHours(2));

        var codes = Enumerable.Range(1, 1_000).Select(number => $"FORM-01-CH-{number:0000}").ToArray();
        var wrong = codes.Where(code => drift.For(code) != TimeSpan.Zero).ToArray();

        // Xấp xỉ một phần mười trong số một nghìn channel của một line thật. Không phải đúng một phần
        // mười — lựa chọn này đến từ một hash — nhưng đủ gần để số lượng drift còn đáng để đọc.
        wrong.Length.ShouldBeInRange(70, 130);

        // Cả hai chiều đều xảy ra. Một thiết bị có thể chạy nhanh hơn gateway cũng như chạy chậm hơn,
        // và code chỉ từng gặp một đồng hồ chạy chậm sẽ có xu hướng mặc định rằng chênh lệch luôn
        // mang một dấu cố định.
        wrong.Select(drift.For).Distinct().Order().ShouldBe([TimeSpan.FromHours(-2), TimeSpan.FromHours(2)]);

        // Các giá trị cố định, và đây là điều khiến chúng đáng được ghi lại: string.GetHashCode được
        // ngẫu nhiên hóa theo từng process, nên một lựa chọn dựa trên nó sẽ pass mọi assertion ở trên
        // mà vẫn giao fault cho các channel khác vào ngày mai. Một run khi đó sẽ không bao giờ có thể
        // lặp lại được.
        var sample = Enumerable.Range(1, 20).Select(number => $"FORM-01-CH-{number:0000}").ToArray();

        sample.Where(code => drift.For(code) != TimeSpan.Zero)
            .ShouldBe(["FORM-01-CH-0001", "FORM-01-CH-0003", "FORM-01-CH-0010", "FORM-01-CH-0015"]);

        drift.For("FORM-01-CH-0003").ShouldBe(TimeSpan.FromHours(-2));
        drift.For("FORM-01-CH-0010").ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task ADropoutIsAGapFollowedByABurstAndNotALoss()
    {
        var time = new FakeTimeProvider(StartedAt);
        var recorder = new RecordingPublisher();

        var publisher = new FaultInjectingPublisher(
            recorder,
            new SimulatorFaults
            {
                DropoutMeanInterval = TimeSpan.FromSeconds(10),
                DropoutDuration = TimeSpan.FromSeconds(30),
            },
            time,
            NullLogger<FaultInjectingPublisher>.Instance);

        await publisher.ConnectAsync(CancellationToken.None);

        var messages = LogicalMessages(200);
        var arrivals = new List<int>(messages.Count);
        var previous = 0;

        foreach (var message in messages)
        {
            await publisher.PublishAsync(message, CancellationToken.None);

            arrivals.Add(recorder.Count - previous);
            previous = recorder.Count;

            time.Advance(TimeSpan.FromSeconds(1));
        }

        await publisher.FlushAsync(CancellationToken.None);

        publisher.Dropouts.ShouldBeGreaterThan(0);
        publisher.HeldHighWater.ShouldBeGreaterThan(1);

        // Không có gì tới khi đường truyền đang gián đoạn...
        arrivals.ShouldContain(0);

        // ...rồi cả backlog tới cùng lúc trong một publish. Đợt dồn đó chính là thứ mà rate limit
        // của C10 tồn tại để chịu đựng; một fault thả backlog ra một cách nhẹ nhàng sẽ không còn gì
        // để chứng minh cả.
        arrivals.ShouldContain(count => count > 1);

        // Một khoảng trống, không phải một mất mát. Mọi thứ được đưa ra đều đã tới, đó là lý do một
        // run kết thúc giữa lúc dropout phải flush trước khi ghi con số cuối cùng của nó.
        recorder.Count.ShouldBe(messages.Count);
    }

    [Fact]
    public async Task TheRunReportSeparatesWhatWasMeasuredFromWhatWasSent()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json");

        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            SamplePeriod = SamplePeriod,
            TimeCompression = 1000,
            ReportPath = reportPath,
            Faults = { DuplicateRate = 0.5, DriftedDeviceRate = 1.0 },
        };

        var time = new FakeTimeProvider(StartedAt);
        var recorder = new RecordingPublisher();
        var line = NewLine(new DeviceClockDrift(options.Faults.DriftedDeviceRate, options.Faults.ClockDrift));

        var publisher = new FaultInjectingPublisher(
            recorder,
            options.Faults,
            time,
            NullLogger<FaultInjectingPublisher>.Instance);

        var worker = new SimulatorWorker(line, publisher, options, time, NullLogger<SimulatorWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => worker.IsRunning, "The simulator never armed its tick loop.");

        const int Ticks = 24;

        for (var tick = 1; tick <= Ticks; tick++)
        {
            time.Advance(options.TickInterval);

            await Eventually.TrueAsync(
                () => worker.ProcessElapsed >= options.SamplePeriod * tick,
                "The simulator did not advance. The tick loop is stuck.");
        }

        await worker.StopAsync(CancellationToken.None);

        var report = RunReportFile.Read(reportPath);
        File.Delete(reportPath);

        // ★ Vế trái của D1. Các tín hiệu mà các channel thực sự đã đo — không phải message, không
        // phải publish, và không bị ảnh hưởng bởi bất kỳ fault nào.
        report.LogicalMeasurements.ShouldBe(line.MeasurementCount);
        report.LogicalMeasurements.ShouldBeGreaterThan(0);

        // Vế phải của cùng phép trừ đó, được giữ tách biệt với nó. Những gì broker nghe được thì lớn
        // hơn, và chênh lệch được nêu rõ ra thay vì để mặc cho người ta tự tính.
        report.LogicalMessages.ShouldBe(worker.LogicalMessageCount);
        report.DuplicateMessages.ShouldBeGreaterThan(0);
        report.PublishedMessages.ShouldBe(report.LogicalMessages + report.DuplicateMessages);
        report.PublishedMessages.ShouldBe(recorder.Count);

        // Được ghi rõ bên cạnh các tổng số để một phép đối soát ra kết quả khớp không thể bị hiểu
        // nhầm là bằng chứng của deduplication trong khi thực ra chưa từng có gì được deduplicate cả
        // (R-M2-1).
        report.FaultsEnabled.ShouldBeTrue();
        report.DriftedDevices.ShouldBe(Channels.Length);
        report.Channels.ShouldBe(Channels.Length);
        report.ProcessElapsed.ShouldBe(SamplePeriod * Ticks);
    }

    [Fact]
    public void SettingsThatDoNotDescribeAFaultAreRefused()
    {
        Should.Throw<InvalidOperationException>(() => new SimulatorFaults { DuplicateRate = 1.5 }.Validate());
        Should.Throw<InvalidOperationException>(() => new SimulatorFaults { DriftedDeviceRate = -0.1 }.Validate());

        // Một dropout không kéo dài chút thời gian nào là một dropout mà không gì quan sát được, nên
        // nó sẽ nằm trong cấu hình trông như đã bật mà chẳng làm gì cả.
        Should.Throw<InvalidOperationException>(() => new SimulatorFaults
        {
            DropoutMeanInterval = TimeSpan.FromSeconds(10),
            DropoutDuration = TimeSpan.Zero,
        }.Validate());

        Should.Throw<ArgumentOutOfRangeException>(() => new DeviceClockDrift(1.5, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void NoFaultsConfiguredMeansTheInjectorIsATunnel()
    {
        // Baseline mà mọi run có fault được so sánh dựa trên. Nếu injector không trong suốt khi mọi
        // thứ đều tắt, phép so sánh sẽ đang đo chính injector chứ không phải gì khác.
        new SimulatorFaults().AnyEnabled.ShouldBeFalse();
        new SimulatorFaults { DuplicateRate = 0.1 }.AnyEnabled.ShouldBeTrue();
        new SimulatorFaults { DriftedDeviceRate = 0.1 }.AnyEnabled.ShouldBeTrue();
        new SimulatorFaults { DropoutMeanInterval = TimeSpan.FromSeconds(1) }.AnyEnabled.ShouldBeTrue();

        DeviceClockDrift.None.For("FORM-01-CH-0003").ShouldBe(TimeSpan.Zero);
        NewLine().DriftedDeviceCount.ShouldBe(0);
    }

    private static Guid[] Identities(IEnumerable<DeviceReading> readings, EquipmentPath path) =>
        [.. readings.Select(reading => reading.NaturalKey(path).SourceEventId.Value)];

    private static FormationLine NewLine(DeviceClockDrift? drift = null) =>
        new(LinePath, Channels, FormationProfile.Default, StartedAt, 0, drift);

    // Cố ý bóc ra thành wire message. Các test này điều khiển PUBLISHER, không phải worker, nên
    // không có gì ở đây xác nhận ngược lại về line — và nếu dùng ComposedMessage sẽ khiến người đọc
    // nghĩ rằng run report đang được cập nhật trong khi thực ra không phải vậy.
    private static List<SparkplugMessage> LogicalMessages(int count)
    {
        var line = NewLine();
        var messages = new List<SparkplugMessage>(line.Connect(TimeSpan.Zero).Select(composed => composed.Message));

        for (var elapsed = SamplePeriod; messages.Count < count; elapsed += SamplePeriod)
        {
            messages.AddRange(line.Advance(elapsed).Select(composed => composed.Message));
        }

        return [.. messages.Take(count)];
    }

    private static List<SparkplugMessage> Advance(FormationLine line, int ticks)
    {
        var messages = new List<SparkplugMessage>();

        for (var tick = 1; tick <= ticks; tick++)
        {
            messages.AddRange(line.Advance(SamplePeriod * tick).Select(composed => composed.Message));
        }

        return messages;
    }

    private static Dictionary<string, MetricAliasTable> AliasesByDevice(IEnumerable<ComposedMessage> births) =>
        births
            .Where(message => message.Topic.MessageType == SparkplugMessageType.DeviceBirth)
            .ToDictionary(
                message => message.Topic.DeviceCode!,
                message => SparkplugPayload.DecodeBirth(message.Payload.AsSpan()).Aliases,
                StringComparer.Ordinal);

    private static async Task<(RecordingPublisher Recorder, FaultInjectingPublisher Publisher)> PublishAllAsync(
        IEnumerable<SparkplugMessage> messages,
        SimulatorFaults faults)
    {
        var recorder = new RecordingPublisher();

        var publisher = new FaultInjectingPublisher(
            recorder,
            faults,
            new FakeTimeProvider(StartedAt),
            NullLogger<FaultInjectingPublisher>.Instance);

        await publisher.ConnectAsync(CancellationToken.None);

        foreach (var message in messages)
        {
            await publisher.PublishAsync(message, CancellationToken.None);
        }

        return (recorder, publisher);
    }
}
