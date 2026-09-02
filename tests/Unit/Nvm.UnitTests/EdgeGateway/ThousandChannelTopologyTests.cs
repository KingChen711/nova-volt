using Microsoft.Extensions.Time.Testing;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Decoding;
using Nvm.EdgeGateway.Sessions;
using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;
using Nvm.Simulator;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.EdgeGateway;

/// <summary>
/// Topology mà `scope.md` §9/M2 đã hứa và chưa từng được chạy: <b>1.000 formation channel dưới một
/// edge node</b>. Demo seed vẫn giữ ở tám vì đó là file mà mọi người đọc để hiểu nhà máy, nên load
/// topology là một catalog riêng — xem `deploy/seed-load/make-load-topology.py`.
/// </summary>
/// <remarks>
/// <para>
/// Cardinality ở đây là vấn đề của ingestion, không phải của simulator. Sparkplug cho một edge node
/// một dòng <c>seq</c> và một <c>bdSeq</c>, nhưng mỗi device dưới nó lại có <b>alias table riêng của
/// chính nó</b>, được khai báo một lần trong <c>DBIRTH</c> của nó và không bao giờ khai báo lại. Một
/// nghìn device là một nghìn table cùng tồn tại song song dưới một session.
/// </para>
/// <para>
/// Điều sẽ xảy ra nếu chúng không được tách biệt là âm thầm và vĩnh viễn cho cả session: alias 7
/// nghĩa là một metric ở channel này và một metric khác ở channel kế tiếp, nên một table dùng chung
/// không fail — nó gán một giá trị nhiệt độ cho một điện áp rồi lưu lại như vậy. Một reading mà không
/// ai nhận ra là sai còn tệ hơn một reading bị từ chối.
/// </para>
/// </remarks>
public sealed class ThousandChannelTopologyTests
{
    private const string LoadSeed = "seed-load";
    private const string DemoSeed = "seed";

    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly DateTimeOffset ReceivedAt = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    private const ulong VoltageAlias = 1;
    private const ulong CapacityAlias = 7;

    [Fact]
    public void TheLoadTopologyModelsAThousandChannels_AndTheDemoSeedStillModelsEight()
    {
        // Cả hai nửa đều quan trọng. Một load fixture âm thầm biến thành demo sẽ đặt một nghìn dòng
        // không ai đọc nổi bằng tay trước mặt người tiếp theo mở factory model ra, còn một demo âm
        // thầm biến thành load fixture sẽ khiến D2 lại chỉ đo có tám channel.
        ChannelsOf(LoadSeed).Length.ShouldBe(1_000);
        ChannelsOf(DemoSeed).Length.ShouldBe(8);
    }

    [Fact]
    public void TheLoadTopologySpreadsChannelsAcrossCyclers_TheWayALineIsBuilt()
    {
        // Mười cycler mỗi cái một trăm, không phải một cycler một nghìn. Không cycler nào trên bất kỳ
        // line nào có một nghìn channel, và số lượng work cell tự nó cũng là một chiều mà gateway
        // phải giải quyết khi biến một topic thành một equipment path.
        var channels = ChannelsOf(LoadSeed);
        var cyclers = channels
            .Select(channel => channel.Segments[4])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        cyclers.Length.ShouldBe(10);
        channels.Select(channel => channel.Value).Distinct(StringComparer.Ordinal).Count().ShouldBe(1_000);
    }

    [Fact]
    public void AThousandDevicesUnderOneEdgeNode_EachKeepTheirOwnAliasTable()
    {
        var clock = new FakeTimeProvider(ReceivedAt);
        var directory = SeededEquipmentDirectory.Load(LoadSeed, requestedRevision: null);
        var decoder = new SparkplugMessageDecoder(new NodeSessionTracker(new GatewayCounters(), clock), directory, clock);
        var channels = ChannelsOf(LoadSeed);

        var sequence = 0UL;

        // Null, và đó là câu trả lời đúng: một NBIRTH mang bdSeq cùng các control metric mà không
        // mang gì máy móc đo được, nên session tracker tiêu thụ nó và không có gì để forward cả.
        // Session vẫn được mở như thường, và đó là điều mà một nghìn birth bên dưới cần tới.
        decoder.Decode(Topic(Line, SparkplugMessageType.NodeBirth), NodeBirth(ref sequence)).ShouldBeNull();

        // Mọi channel đều khai báo voltage. Chỉ channel đầu tiên khai báo thêm capacity, và đó chính
        // là điều chứng minh các table thực sự tách biệt ở vài dòng bên dưới.
        foreach (var channel in channels)
        {
            var declaresCapacity = channel == channels[0];
            var birth = decoder.Decode(
                Topic(channel, SparkplugMessageType.DeviceBirth),
                DeviceBirth(ref sequence, declaresCapacity));

            birth.ShouldNotBeNull();
            birth.EquipmentPath.ShouldBe(channel);
        }

        // Data chỉ mang alias trên cả một nghìn channel. Đây chính là toàn bộ mục đích của một birth:
        // không gì trong các payload này nói "voltage" cả, vậy mà mỗi payload vẫn giải ra đúng nó.
        foreach (var channel in channels)
        {
            var data = decoder.Decode(
                Topic(channel, SparkplugMessageType.DeviceData),
                AliasOnlyData(ref sequence, VoltageAlias));

            data.ShouldNotBeNull();
            data.Readings.ShouldHaveSingleItem().MetricName.ShouldBe("Formation/Voltage");
        }

        // Các table là theo từng device, không phải một table dùng chung cho cả node. Alias 7 chỉ
        // được khai báo trên channel đầu tiên, nên nó đọc được ở đó và bị từ chối ở mọi nơi khác -
        // và từ chối chính là kết quả đúng, bởi vì lựa chọn thay thế là gán metric của channel này
        // cho channel khác rồi lưu nó như thể đã thực sự đo được.
        decoder.Decode(
                Topic(channels[0], SparkplugMessageType.DeviceData),
                AliasOnlyData(ref sequence, CapacityAlias))
            .ShouldNotBeNull()
            .Readings.ShouldHaveSingleItem()
            .MetricName.ShouldBe("Formation/Capacity");

        Should.Throw<UnknownMetricAliasException>(() => decoder.Decode(
            Topic(channels[1], SparkplugMessageType.DeviceData),
            AliasOnlyData(ref sequence, CapacityAlias)));
    }

    private static EquipmentPath[] ChannelsOf(string seedDirectory)
    {
        var catalog = FactoryModelSeed.LoadCatalog(seedDirectory);
        var snapshot = catalog.Find(catalog.LatestRevision)
            ?? throw new InvalidOperationException($"'{seedDirectory}' has no revision to read.");

        return [.. FormationChannels.Under(snapshot, Line)];
    }

    private static string Topic(EquipmentPath path, SparkplugMessageType messageType) =>
        SparkplugTopic.For(path, messageType).Value;

    // seq đếm xuyên suốt cả session, cả birth lẫn data như nhau, đúng như cách một node thật đánh số.
    private static ulong Next(ref ulong sequence)
    {
        var current = sequence;
        sequence = (sequence + 1) % 256;

        return current;
    }

    private static byte[] NodeBirth(ref ulong sequence) =>
        SparkplugPayload.EncodeBirth(
            [
                new DeviceReading(
                    SparkplugPayload.BirthDeathSequenceMetric,
                    Alias: null,
                    new MetricValue.Integral(1),
                    ReceivedAt),
            ],
            Next(ref sequence),
            ReceivedAt);

    private static byte[] DeviceBirth(ref ulong sequence, bool declaresCapacity)
    {
        List<DeviceReading> readings =
        [
            new("Formation/Voltage", VoltageAlias, new MetricValue.Real(3.7), ReceivedAt),
        ];

        if (declaresCapacity)
        {
            readings.Add(new DeviceReading("Formation/Capacity", CapacityAlias, new MetricValue.Real(4.8), ReceivedAt));
        }

        return SparkplugPayload.EncodeBirth(readings, Next(ref sequence), ReceivedAt);
    }

    private static byte[] AliasOnlyData(ref ulong sequence, ulong alias) =>
        SparkplugPayload.EncodeData(
            [new DeviceReading(MetricName: string.Empty, alias, new MetricValue.Real(3.71), ReceivedAt)],
            Next(ref sequence),
            ReceivedAt);
}
