using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Reporting;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Simulator;

/// <summary>Nén thời gian phải chỉ làm thay đổi thời lượng một run mất, không gì khác.</summary>
/// <remarks>
/// Một formation cycle kéo dài mười tám giờ và không test nào có thể chờ mười tám giờ — nhưng một
/// test đạt được điều đó bằng cách bỏ qua sample thì lại đang đo một nhà máy khác. Số lượng
/// measurement là vế trái của phép đối soát D1, nên nó phải sống sót qua việc nén một cách chính xác.
/// </remarks>
public sealed class SimulatorWorkerTests
{
    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

    private static readonly EquipmentPath[] Channels =
    [
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001"),
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0002"),
    ];

    private static readonly DateTimeOffset StartedAt = new(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AThousandTimesFasterIsTheSameRun()
    {
        var slow = await RunOneCycleAsync(compression: 1);
        var fast = await RunOneCycleAsync(compression: 1000);

        // Cùng một nhà máy y hệt.
        fast.Messages.ShouldBe(slow.Messages);
        fast.Measurements.ShouldBe(slow.Measurements);
        fast.ProcessElapsed.ShouldBe(slow.ProcessElapsed);
        fast.ProcessElapsed.ShouldBe(FormationProfile.Default.CycleDuration);

        // Một phần nghìn của đồng hồ. Đây là thứ duy nhất mà việc nén được phép làm thay đổi, và nó
        // được kiểm tra thay vì mặc định là đúng — một phép nén âm thầm không làm gì cả sẽ khiến mọi
        // assertion ở trên vẫn pass trong khi một test mười tám giờ vẫn mất mười tám giờ.
        slow.ClockElapsed.ShouldBe(FormationProfile.Default.CycleDuration);
        fast.ClockElapsed.ShouldBe(slow.ClockElapsed / 1000);
    }

    [Fact]
    public async Task EveryMessageOfARunNamesAPlaceOnThisLine()
    {
        var run = await RunOneCycleAsync(compression: 1000);

        run.Topics.ShouldAllBe(topic => topic.LinePath == LinePath);
        run.Topics.Where(topic => topic.DeviceCode is not null)
            .ShouldAllBe(topic => Channels.Any(channel => channel.Code == topic.DeviceCode));
    }

    [Fact]
    public async Task ARebirthRequest_MakesTheNodeDeclareItselfAgain()
    {
        // Thiếu điều này, gateway sẽ mù cho suốt phần còn lại của run. Nó subscribe một giây sau khi
        // simulator đã publish các birth của mình, không đọc được bất kỳ message chỉ-mang-alias nào
        // sau đó, yêu cầu rebirth ở mỗi khoảng trống — và nếu không gì trả lời, DBIRTH kế tiếp cách
        // đó một lần đổi cell, mà trên một formation line là mười tám giờ. Đo được trước khi điều này
        // tồn tại: hơn 10.000 message bị từ chối chỉ trong vài phút.
        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            SamplePeriod = TimeSpan.FromMinutes(30),
            TimeCompression = 1000,
            ReportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json"),
        };
        var line = new FormationLine(LinePath, Channels, FormationProfile.Default, StartedAt);
        var recorder = new RecordingPublisher();
        var time = new FakeTimeProvider(StartedAt);
        var publisher = new FaultInjectingPublisher(
            recorder,
            new SimulatorFaults(),
            time,
            NullLogger<FaultInjectingPublisher>.Instance);
        using var worker = new SimulatorWorker(line, publisher, options, time, NullLogger<SimulatorWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => worker.IsRunning, "The simulator never armed its tick loop.");

        var afterConnect = recorder.Messages.Count;
        var measurementsBeforeRebirth = line.MeasurementCount;
        recorder.RebirthRequested.ShouldNotBeNull();

        await recorder.RebirthRequested!(CancellationToken.None);

        // Một NBIRTH và một DBIRTH cho mỗi channel, y hệt như lúc connect.
        (recorder.Messages.Count - afterConnect).ShouldBe(Channels.Length + 1);
        worker.Rebirths.ShouldBe(1);

        // Được publish lại, không phải đo lại. Rebirth công bố lại cùng một cell tại cùng một thời
        // điểm, nên nó mang cùng natural key và deduplication chỉ lưu nó một lần. Một run report đếm
        // nó lần nữa sẽ khiến vế trái của D1 vượt vế phải một DBIRTH đầy đủ cho mỗi rebirth — trước
        // khi điều này được giữ vững thì đo được chính xác -48 trên một line tám channel.
        line.MeasurementCount.ShouldBe(measurementsBeforeRebirth);

        // Một consumer bỏ lỡ các birth sẽ hỏi một lần cho mỗi khoảng trống phát hiện được, nên nó hỏi
        // hàng nghìn lần trước khi câu trả lời đầu tiên tới được nó. Trả lời từng lần một sẽ nhấn
        // chìm dữ liệu mà nó muốn đọc.
        await recorder.RebirthRequested!(CancellationToken.None);
        worker.Rebirths.ShouldBe(1);

        await worker.StopAsync(CancellationToken.None);
        File.Delete(options.ReportPath);
    }

    [Fact]
    public async Task AGracefulStopFinishesTheBatchItHasAlreadyComposed()
    {
        // Nhà máy đang ở đâu khi một lệnh stop tới: các channel đã được đọc, các deadband đã dịch
        // chuyển, và nửa batch vẫn đang trên đường đi ra. Bỏ phần còn lại sẽ là cách shutdown rẻ nhất
        // có thể, và nó sẽ đặt các reading vào vế trái của D1 mà không database nào từng được cấp —
        // lab D3 cố ý dừng nguồn, nên nó đứng đúng ngay đây gần như mọi run.
        //
        // Thay vào đó, nhà máy dừng giữa các TICK, nơi chưa gì được đo cả và việc dừng lại không tốn
        // gì hết.
        var rig = Rig();

        await rig.Worker.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Worker.IsRunning, "The simulator never armed its tick loop.");

        // Đứng bên trong batch thay vì phải chạy đua để bắt kịp nó: publish đầu tiên của tick kế tiếp
        // được giữ mở cho tới khi test này thả nó ra.
        rig.Link.CatchNext();
        rig.Time.Advance(rig.Options.TickInterval);

        await rig.Link.Caught;

        var caughtAt = rig.Link.Count;
        var stopping = rig.Worker.StopAsync(CancellationToken.None);

        rig.Link.Release();

        await stopping;

        // Message đã bị giữ lại, cộng thêm ít nhất một message được soạn phía sau nó mà lệnh cancel
        // có thể đã lấy đi. Thiếu message thứ hai này, test sẽ pass trên một batch rỗng.
        (rig.Link.Count - caughtAt).ShouldBeGreaterThan(1);

        rig.Worker.AbandonedMeasurements.ShouldBe(0);
        rig.Worker.LogicalMessageCount.ShouldBe(rig.Link.Count);

        // Cả hai vế, và không vế nào được suy ra từ vế còn lại: những gì một consumer có thể lưu được
        // từ các payload, đối chiếu với những gì nhà máy nói các channel của nó đã đo.
        var ledger = new MeasurementLedger();

        ledger.AddAll(rig.Link.Messages);
        ledger.Total.ShouldBe(rig.Line.MeasurementCount);

        var report = rig.ReadReport();

        report.LogicalMeasurements.ShouldBe(rig.Line.MeasurementCount);
        report.AbandonedMeasurements.ShouldBe(0);
    }

    [Fact]
    public async Task ALinkThatDiesMidBatchCannotMakeTheReconciliationComeOutEven()
    {
        // Nửa còn lại của J7, và là nửa quyết định liệu oracle có thấy được gì hay không. Một publish
        // ném ra exception sẽ bỏ lại phần còn lại của một batch đã soạn nằm trên sàn: những reading
        // đó đã được đo, và một database sẽ không bao giờ được cấp chúng.
        //
        // Nếu vế trái được đếm sau khi publish, nó sẽ giảm đúng bằng lượng mà vế phải sắp thiếu hụt —
        // và D1 sẽ ra kết quả khớp trên một nhà máy đã mất dữ liệu. Điều phải xảy ra thay vào đó là:
        // con số đếm giữ nguyên, chênh lệch hiện ra, và gate chuyển đỏ với một con số nói rõ mất mát
        // nằm ở phía nào của đường truyền.
        var rig = Rig();

        await rig.Worker.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Worker.IsRunning, "The simulator never armed its tick loop.");

        // Thêm một message nữa đi qua được, còn phần còn lại của batch phía sau thì không.
        rig.Link.FailAfter(rig.Link.Count + 1);
        rig.Time.Advance(rig.Options.TickInterval);

        await Eventually.TrueAsync(
            () => rig.Worker.AbandonedMeasurements > 0,
            "The dead link never cut into a batch, so this proves nothing.");

        await rig.Worker.StopAsync(CancellationToken.None);

        var ledger = new MeasurementLedger();

        ledger.AddAll(rig.Link.Messages);

        // Được nêu ra như một phương trình vì đó chính là điều gate phải có khả năng nói ra: những gì
        // nhà máy đã đo, trừ đi những gì đường truyền đã mang theo, chính là mất mát — được báo cáo,
        // không phải bị bù trừ đi mất.
        (rig.Line.MeasurementCount - ledger.Total).ShouldBe(rig.Worker.AbandonedMeasurements);

        var report = rig.ReadReport();

        report.LogicalMeasurements.ShouldBe(rig.Line.MeasurementCount);
        report.AbandonedMeasurements.ShouldBe(rig.Worker.AbandonedMeasurements);

        // Cái pass giả mà điều này tồn tại để ngăn chặn. Một database chứa mọi thứ đường truyền đã
        // mang theo vẫn còn thiếu so với report, nên phép trừ mà D1 thực hiện không thể ra bằng
        // không.
        report.LogicalMeasurements.ShouldBeGreaterThan(ledger.Total);
    }

    [Fact]
    public void SettingsThatCannotProduceAPlantAreRefusedBeforeAnythingConnects()
    {
        // Mỗi cái trong số này nếu không sẽ thất bại ở đâu đó giữa một run, nơi nguyên nhân khó nhìn
        // thấy hơn rất nhiều so với ở đây.
        Should.Throw<InvalidOperationException>(() => new SimulatorOptions { TimeCompression = 0 }.Validate());
        Should.Throw<InvalidOperationException>(() => new SimulatorOptions { SamplePeriod = TimeSpan.Zero }.Validate());

        // 18 giờ không phải là một số nguyên lần sample 7 phút, nên sample cuối cùng của một cell sẽ
        // nằm gần sample đầu tiên của cell kế tiếp hơn bất kỳ cặp nào khác trong run.
        Should.Throw<InvalidOperationException>(
            () => new SimulatorOptions { SamplePeriod = TimeSpan.FromMinutes(7) }.Validate());

        // Dưới một millisecond, một timer không còn theo kịp nữa và run trở nên chậm hơn những gì
        // compression tuyên bố — điều này sẽ biến một con số throughput thành một lời nói dối.
        Should.Throw<InvalidOperationException>(
            () => new SimulatorOptions { SamplePeriod = TimeSpan.FromSeconds(1), TimeCompression = 5000 }.Validate());
    }

    // Một worker với một đường truyền mà test có thể đứng bên trong, được nối dây đúng theo cách
    // simulator thực sự chạy: fault injector nằm trên đường đi với mọi tỷ lệ bằng không, vì
    // pass-through đó cũng chính là thứ một run không lỗi phải đi qua.
    private static WorkerRig Rig()
    {
        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            SamplePeriod = TimeSpan.FromMinutes(30),
            TimeCompression = 1000,
            ReportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json"),
        };

        var time = new FakeTimeProvider(StartedAt);
        var link = new InterruptiblePublisher();
        var line = new FormationLine(LinePath, Channels, FormationProfile.Default, StartedAt);

        var publisher = new FaultInjectingPublisher(
            link,
            options.Faults,
            time,
            NullLogger<FaultInjectingPublisher>.Instance);

        return new WorkerRig(
            new SimulatorWorker(line, publisher, options, time, NullLogger<SimulatorWorker>.Instance),
            line,
            link,
            time,
            options);
    }

    private sealed record WorkerRig(
        SimulatorWorker Worker,
        FormationLine Line,
        InterruptiblePublisher Link,
        FakeTimeProvider Time,
        SimulatorOptions Options)
    {
        // Đọc từ file thay vì từ worker. D1 và D3 chỉ đọc file này và không gì khác, nên một counter
        // đúng trong bộ nhớ nhưng thiếu trong JSON vẫn sẽ khiến cả hai lab so sánh một con số chúng
        // chưa từng thấy.
        public RunReport ReadReport()
        {
            var report = RunReportFile.Read(Options.ReportPath);

            File.Delete(Options.ReportPath);

            return report;
        }
    }

    private static async Task<RunResult> RunOneCycleAsync(double compression)
    {
        var options = new SimulatorOptions
        {
            LinePath = LinePath.Value,
            SamplePeriod = TimeSpan.FromMinutes(30),
            TimeCompression = compression,
            ReportPath = Path.Combine(Path.GetTempPath(), $"nvm-sim-{Guid.NewGuid():N}.json"),
        };

        var time = new FakeTimeProvider(StartedAt);
        var recorder = new RecordingPublisher();
        var line = new FormationLine(LinePath, Channels, FormationProfile.Default, StartedAt);

        // Đi qua fault injector với mọi tỷ lệ bằng không, vì đó chính là cách sắp xếp mà simulator
        // thực sự chạy trong đó. Test worker với một publisher trần trụi sẽ bỏ qua việc kiểm thử
        // pass-through đúng ngay chỗ nó quan trọng nhất: run không có fault chính là baseline mà mọi
        // run có fault được so sánh dựa trên.
        var publisher = new FaultInjectingPublisher(
            recorder,
            options.Faults,
            time,
            NullLogger<FaultInjectingPublisher>.Instance);

        var worker = new SimulatorWorker(line, publisher, options, time, NullLogger<SimulatorWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        // Đẩy một fake clock đi trước khi tick loop được nạp sẵn sẽ đưa thời gian của nhà máy vượt
        // qua một tick mà không gì đang giữ, và run âm thầm thiếu mất một sample. Thời gian thật
        // không thể tạo ra một tick sớm như vậy, nên đây là một rủi ro do fake clock gây ra và fake
        // clock phải chịu trách nhiệm cho nó.
        await Eventually.TrueAsync(() => worker.IsRunning, "The simulator never armed its tick loop.");

        var ticks = (int)(FormationProfile.Default.CycleDuration / options.SamplePeriod);

        for (var tick = 1; tick <= ticks; tick++)
        {
            time.Advance(options.TickInterval);

            await Eventually.TrueAsync(
                () => worker.ProcessElapsed >= options.SamplePeriod * tick,
                "The simulator did not advance. The tick loop is stuck.");
        }

        await worker.StopAsync(CancellationToken.None);

        File.Delete(options.ReportPath);

        return new RunResult(
            recorder.Messages.Count,
            line.MeasurementCount,
            worker.ProcessElapsed,
            time.GetUtcNow() - StartedAt,
            [.. recorder.Messages.Select(message => message.Topic)]);
    }

    private sealed record RunResult(
        int Messages,
        long Measurements,
        TimeSpan ProcessElapsed,
        TimeSpan ClockElapsed,
        IReadOnlyList<SparkplugTopic> Topics);
}
