using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Simulator.Formation;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Simulator;

/// <summary>Những gì line publish, và tính chất khiến time compression trở nên trung thực.</summary>
public sealed class FormationLineTests
{
    // Voltage, current, temperature, capacity, step và cell serial. Serial cũng là một trong số đó:
    // gateway forward nó và pipeline lưu một dòng cho nó như bất kỳ metric đã khai báo nào khác.
    private const int ReadingsPerDeclaration = 6;

    private static readonly EquipmentPath LinePath = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

    private static readonly ImmutableArray<EquipmentPath> Channels =
    [
        .. Enumerable.Range(1, 4).Select(number =>
            EquipmentPath.Parse($"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-{number:0000}")),
    ];

    private static readonly DateTimeOffset StartedAt = new(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComingOnlineIsANodeBirthFollowedByADeviceBirthPerChannel()
    {
        var messages = Line().Connect(TimeSpan.Zero);

        messages.Length.ShouldBe(Channels.Length + 1);
        messages[0].Topic.MessageType.ShouldBe(SparkplugMessageType.NodeBirth);
        messages[0].Topic.DeviceCode.ShouldBeNull();

        // Thứ tự này không phải trang trí: một device birth tới trước node birth của nó sẽ thuộc về
        // một session mà consumer chưa từng nghe tới, và đó chính là điều C11 yêu cầu rebirth để xử lý.
        messages.Skip(1).ShouldAllBe(message => message.Topic.MessageType == SparkplugMessageType.DeviceBirth);

        messages.Skip(1).Select(message => message.Topic.DeviceCode)
            .ShouldBe(Channels.Select(channel => channel.Code));
    }

    [Fact]
    public void TheNodeBirthCarriesBdSeqWithoutAnAlias()
    {
        // bdSeq phải đọc được trong một NDEATH mà broker publish như một last will, và một last will
        // được soạn trước birth — thứ lẽ ra đã gán một alias. Gán alias cho nó sẽ khiến death
        // certificate không đọc được — và một death không ai đọc được là một node trông như vẫn sống.
        var birth = SparkplugPayload.DecodeBirth(Line(birthDeathSequence: 7).Connect(TimeSpan.Zero)[0].Payload.AsSpan());

        birth.Readings.Select(reading => reading.MetricName).ShouldBe(["bdSeq", "Node Control/Rebirth"]);
        birth.Readings.ShouldAllBe(reading => reading.Alias == null);
        birth.Readings[0].Value.ShouldBe(new MetricValue.Integral(7));
        birth.Aliases.Count.ShouldBe(0);
    }

    [Fact]
    public void ADeviceBirthDeclaresEveryMetricAndTheDataThatFollowsUsesTheAliases()
    {
        var line = Line();
        var birthMessages = line.Connect(TimeSpan.Zero);

        var birth = SparkplugPayload.DecodeBirth(birthMessages[1].Payload.AsSpan());

        birth.Readings.Select(reading => reading.MetricName).ShouldBe(
        [
            "Formation/Voltage",
            "Formation/Current",
            "Formation/Temperature",
            "Formation/Capacity",
            "Formation/StepIndex",
            "Formation/CellSerial",
        ]);

        // Vòng lặp khép kín quan trọng: những gì simulator ghi ra, decoder đọc lại — và update theo
        // sau không đọc được nếu thiếu birth, y hệt như một thiết bị thật.
        var update = line.Advance(TimeSpan.FromMinutes(30))
            .First(message => message.Topic.DeviceCode == Channels[0].Code);

        var readings = SparkplugPayload.DecodeData(update.Payload.AsSpan(), birth.Aliases);

        readings.ShouldNotBeEmpty();
        readings.ShouldAllBe(reading => reading.MetricName.StartsWith("Formation/", StringComparison.Ordinal));
        Should.Throw<UnknownMetricAliasException>(
            () => SparkplugPayload.DecodeData(update.Payload.AsSpan(), MetricAliasTable.Empty));
    }

    [Fact]
    public void TheCellSerialIsOneAPlantCouldHaveEngraved()
    {
        var birth = SparkplugPayload.DecodeBirth(Line().Connect(TimeSpan.Zero)[1].Payload.AsSpan());

        var serial = birth.Readings.Single(reading => reading.MetricName == "Formation/CellSerial").Value;

        var text = serial.ShouldBeOfType<MetricValue.Text>().Value;

        SerialNumber.TryParse(text, out var parsed).ShouldBeTrue();
        parsed.SiteCode.ShouldBe("NV1");
        parsed.LineCode.ShouldBe("F1");
    }

    [Fact]
    public void ChannelsAreStaggeredAcrossTheCycleRatherThanStartedTogether()
    {
        // Một line nạp cell liên tục, nên tại bất kỳ thời điểm nào cũng có channel đang sạc và channel
        // đang nghỉ. Một line mà cả bốn channel chuyển stage cùng một thời điểm sẽ tạo ra một traffic
        // shape mà downstream sẽ không bao giờ gặp lại.
        var line = Line();
        var births = line.Connect(TimeSpan.Zero);

        var steps = births
            .Skip(1)
            .Select(message => SparkplugPayload.DecodeBirth(message.Payload.AsSpan()))
            .Select(birth => birth.Readings.Single(reading => reading.MetricName == "Formation/StepIndex").Value)
            .Distinct()
            .Count();

        steps.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void ANewCellProducesAFreshDeviceBirth()
    {
        // Một cycler publish DBIRTH ở đầu mỗi cell: serial đã thay đổi, và birth là message duy nhất
        // mang theo tên.
        var line = Line();

        line.Connect(TimeSpan.Zero);

        var oneCycleOn = line.Advance(FormationProfile.Default.CycleDuration);

        oneCycleOn.ShouldAllBe(message => message.Topic.MessageType == SparkplugMessageType.DeviceBirth);
        oneCycleOn.Length.ShouldBe(Channels.Length);
    }

    [Fact]
    public void ReportByExceptionMeansMostChannelsSayNothingMostOfTheTime()
    {
        // Traffic shape mà cả protocol tồn tại vì nó. Nếu mọi channel đều publish ở mọi sample thì
        // alias sẽ vô nghĩa, và deadband cũng vậy.
        var line = Line();

        line.Connect(TimeSpan.Zero);

        var published = 0;
        var samples = 0;

        for (var elapsed = TimeSpan.FromSeconds(5); elapsed <= TimeSpan.FromHours(4); elapsed += TimeSpan.FromSeconds(5))
        {
            published += line.Advance(elapsed).Length;
            samples += Channels.Length;
        }

        published.ShouldBeLessThan(samples);
        published.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void RunningTheSameCycleTwiceProducesTheSameMeasurements()
    {
        // Tính chất mà time compression dựa vào. Mọi thứ line publish đều là một hàm của process
        // time, nên một run có thể tái lập được — và một run bị nén là cùng một run, chỉ xong sớm
        // hơn. SimulatorWorkerTests là nơi clock thực sự bị nén.
        RunOneCycle().ShouldBe(RunOneCycle());
    }

    [Fact]
    public void SamplingTwiceAsOftenMeasuresMoreRatherThanDifferently()
    {
        // Nói rõ ra để invariant ở trên không bị hiểu nhầm thành một invariant rộng hơn. Sample period
        // là một quyết định thật về độ phân giải; compression factor thì không.
        var coarse = RunOneCycle(TimeSpan.FromMinutes(10));
        var fine = RunOneCycle(TimeSpan.FromMinutes(5));

        fine.Measurements.ShouldBeGreaterThan(coarse.Measurements);
    }

    [Fact]
    public void AnEdgeNodeSpeaksForALineAndNotForAWorkCell()
    {
        Should.Throw<ArgumentException>(() => new FormationLine(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01"),
            Channels,
            FormationProfile.Default,
            StartedAt));
    }

    [Fact]
    public void ALineWithNoChannelsIsRefused()
    {
        // Một danh sách rỗng nghĩa là line đã ngừng hoạt động hoặc chưa từng được triển khai, và một
        // simulator vẫn khởi động sẽ ngồi đó publish không gì cả mà vẫn trông khỏe mạnh.
        Should.Throw<ArgumentException>(() => new FormationLine(
            LinePath,
            [],
            FormationProfile.Default,
            StartedAt));
    }

    [Fact]
    public void TheCountIsEveryReadingTheChannelsTook()
    {
        // Vế trái của D1, được pin theo cấu trúc. Một birth khai báo sáu reading, và một con số "năm"
        // hard-code cạnh mảng đó từng khiến run report báo thiếu một reading mỗi DBIRTH — cho mọi
        // channel, suốt cả run. Không gì thất bại cả: việc đối soát đơn giản là ra kết quả thiếu và
        // đọc như một sự mất dữ liệu. Đếm dựa trên mảng thì không thể lệch khỏi chính mảng đó.
        //
        // Ở đây được đếm ra từ các payload, giống cách pipeline đếm dòng, nên cái được so sánh là
        // "những gì một consumer có thể lưu được" đối chiếu với "những gì nhà máy nói nó đã đo" — hai
        // vế mà D1 lấy hiệu. So sánh counter của line với một con số được suy ra từ chính counter đó
        // sẽ pass với bất kỳ phép tính nào.
        var line = Line();
        var ledger = new MeasurementLedger();

        ledger.AddAll(line.Connect(TimeSpan.Zero).Select(composed => composed.Message));

        for (var elapsed = TimeSpan.FromMinutes(5);
            elapsed <= FormationProfile.Default.CycleDuration;
            elapsed += TimeSpan.FromMinutes(5))
        {
            ledger.AddAll(line.Advance(elapsed).Select(composed => composed.Message));
        }

        ledger.Total.ShouldBe(line.MeasurementCount);
    }

    [Fact]
    public void ReadingTheChannelIsWhatMakesAMeasurement()
    {
        // J7, được pin đúng theo cách nhà máy vận hành. Instrument đã đọc cell xong vào lúc Advance
        // trả về: trạng thái deadband đã dịch chuyển, nên sample kế tiếp so sánh với một giá trị mà
        // sample này đã tạo ra, và reading này không bao giờ có thể được đo lại. Việc message mang nó
        // có tới được broker hay không là chuyện của đường truyền, và một batch bị rớt sau đó là một
        // LOSS chứ không phải một measurement chưa từng xảy ra.
        //
        // Đếm ở phía bên kia của publish trông có vẻ chặt chẽ hơn nhưng lại là điều ngược lại: các
        // reading sẽ rời khỏi vế trái của D1 đúng vào lúc các dòng lẽ ra chúng nợ lại không tới được
        // vế phải, nên phép so sánh bằng sẽ đúng trên chính dữ liệu mà nhà máy đã đo nhưng không ai có
        // được. Một sự cố broker kéo dài 40 giây khi đó sẽ đối soát khớp tuyệt đối, và đường cong
        // formation của một cell trong một lô bị thu hồi sẽ có một lỗ hổng mà không bản ghi nào gọi
        // đó là mất dữ liệu.
        var line = Line();

        line.Connect(TimeSpan.Zero);

        var afterBirth = line.MeasurementCount;

        afterBirth.ShouldBe(Channels.Length * ReadingsPerDeclaration);

        var dropped = line.Advance(TimeSpan.FromMinutes(30));

        // Batch này là thật và mang theo các reading thật — đây không phải một test cho một tick rỗng.
        dropped.ShouldNotBeEmpty();
        dropped.Sum(message => message.Measurements).ShouldBeGreaterThan(0);

        // Đã được soạn, chưa từng được publish, nhưng vẫn được tính là đã đo. Con số đếm là của nhà
        // máy, không phải của đường truyền.
        line.MeasurementCount.ShouldBe(afterBirth + dropped.Sum(message => message.Measurements));
    }

    [Fact]
    public void ARebirthRestatesAChannelRatherThanMeasuringItAgain()
    {
        // Cùng cell, cùng giá trị, cùng device clock — do đó cùng natural key, và database hoàn toàn
        // đúng khi chỉ lưu một lần. Đếm lần thứ hai ở đây sẽ khiến vế trái của D1 vượt vế phải đúng
        // một DBIRTH đầy đủ cho mỗi channel mỗi rebirth: đo được chính xác -48 trên một line tám
        // channel. Một lần công bố lại một measurement không phải là một measurement khác.
        //
        // Nửa còn lại ở cấp độ line của điều này. SimulatorWorkerTests kiểm chứng cùng tính chất đó
        // qua worker, nơi rebirth tới trên một MQTT thread thay vì được gọi trực tiếp.
        var line = Line();

        line.Connect(TimeSpan.Zero);

        var afterBirth = line.MeasurementCount;
        var rebirth = line.Connect(TimeSpan.Zero);

        rebirth.Length.ShouldBe(Channels.Length + 1);
        rebirth.Sum(message => message.Measurements).ShouldBe(0);
        line.MeasurementCount.ShouldBe(afterBirth);

        // Và một rebirth không phải là một bức tường quanh channel: reading thật tiếp theo vẫn được
        // tính.
        line.Advance(TimeSpan.FromMinutes(30)).Sum(message => message.Measurements).ShouldBeGreaterThan(0);
        line.MeasurementCount.ShouldBeGreaterThan(afterBirth);
    }

    private static FormationLine Line(ulong birthDeathSequence = 0) =>
        new(LinePath, Channels, FormationProfile.Default, StartedAt, birthDeathSequence);

    private static (int Messages, long Measurements) RunOneCycle(TimeSpan? samplePeriod = null)
    {
        var period = samplePeriod ?? TimeSpan.FromMinutes(5);
        var line = Line();
        var messages = line.Connect(TimeSpan.Zero).Length;

        for (var elapsed = period; elapsed <= FormationProfile.Default.CycleDuration; elapsed += period)
        {
            messages += line.Advance(elapsed).Length;
        }

        return (messages, line.MeasurementCount);
    }
}
