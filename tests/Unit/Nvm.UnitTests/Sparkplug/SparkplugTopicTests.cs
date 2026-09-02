using Nvm.Kernel.Identity;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Đọc và ghi Sparkplug topic, không hỏi nhà máy điều gì.</summary>
public sealed class SparkplugTopicTests
{
    private const string DeviceTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142";
    private const string NodeTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/NBIRTH/EDGE-F1";

    [Fact]
    public void ADeviceTopicNamesAPlaceInTheIsa95Tree()
    {
        var topic = SparkplugTopic.Parse(DeviceTopic);

        topic.MessageType.ShouldBe(SparkplugMessageType.DeviceData);
        topic.EnterpriseCode.ShouldBe("NOVAVOLT");
        topic.SiteId.ShouldBe("NV1");
        topic.AreaCode.ShouldBe("FORMATION");
        topic.LineCode.ShouldBe("F1");
        topic.DeviceCode.ShouldBe("FORM-01-CH-0142");
        topic.LinePath.Value.ShouldBe("NOVAVOLT/NV1/FORMATION/F1");
        topic.GroupId.ShouldBe("NOVAVOLT-NV1-FORMATION");
        topic.EdgeNodeId.ShouldBe("EDGE-F1");
    }

    [Fact]
    public void ANodeTopicHasNoDeviceAndStopsAtTheLine()
    {
        var topic = SparkplugTopic.Parse(NodeTopic);

        topic.MessageType.ShouldBe(SparkplugMessageType.NodeBirth);
        topic.DeviceCode.ShouldBeNull();
        topic.LinePath.Kind.ShouldBe(FactoryNodeKind.Line);
    }

    [Fact]
    public void APlaceInThePlantKnowsTheTopicItPublishesOn()
    {
        // Hướng mà C05 cần: simulator publish với tư cách nhà máy, nên bắt đầu từ path.
        SparkplugTopic
            .For(EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142"), SparkplugMessageType.DeviceData)
            .Value
            .ShouldBe(DeviceTopic);

        SparkplugTopic
            .For(EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1"), SparkplugMessageType.NodeBirth)
            .Value
            .ShouldBe(NodeTopic);
    }

    [Fact]
    public void WritingATopicAndReadingItBackGivesTheSameTopic()
    {
        // Round-trip khép kín không cần factory model. Lưu ý điều nó không chứng minh: work cell
        // FORM-01 mất khỏi topic và không thể quay lại từ đó — chỉ directory mới trả về path sáu segment,
        // là điều SparkplugTopicResolutionTests kiểm.
        foreach (var (path, messageType) in new (string Path, SparkplugMessageType Type)[]
        {
            ("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142", SparkplugMessageType.DeviceData),
            ("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01", SparkplugMessageType.DeviceBirth),
            ("NOVAVOLT/DE1/PACK/P1", SparkplugMessageType.NodeDeath),
            ("NOVAVOLT/NV1/AGING/A1", SparkplugMessageType.NodeCommand),
        })
        {
            var written = SparkplugTopic.For(EquipmentPath.Parse(path), messageType);

            SparkplugTopic.Parse(written.Value).ShouldBe(written);
        }
    }

    [Fact]
    public void AnAreaCodeMayContainAHyphenBecauseTheAreaTakesTheRemainder()
    {
        // Group id nối ba code bằng ký tự mà một code cũng có thể chứa. Tách đúng thành ba, với phần dư
        // đứng cuối, giúp nó reversible cho code duy nhất được phép là compound.
        var written = SparkplugTopic.For(
            EquipmentPath.Parse("NOVAVOLT/NV1/CELL-FINISHING/F1"),
            SparkplugMessageType.NodeData);

        written.Value.ShouldBe("spBv1.0/NOVAVOLT-NV1-CELL-FINISHING/NDATA/EDGE-F1");
        SparkplugTopic.Parse(written.Value).AreaCode.ShouldBe("CELL-FINISHING");
    }

    [Fact]
    public void AnEnterpriseOrSiteCodeWithAHyphenIsRefusedWhereItIsStillVisible()
    {
        // Bắt lúc ghi vì lúc đọc không thể phát hiện: "NOVA-VOLT-NV1-FORMATION" tách thành enterprise
        // NOVA, site VOLT, area NV1-FORMATION, là topic well-formed hoàn hảo của nhà máy không tồn tại.
        // Failure chỉ lộ muộn hơn nhiều thành "this line is not in the model".
        var thrown = Should.Throw<ArgumentException>(() => SparkplugTopic.For(
            EquipmentPath.Parse("NOVA-VOLT/NV1/FORMATION/F1"),
            SparkplugMessageType.NodeData));

        thrown.Message.ShouldContain("NOVA-VOLT");
    }

    [Fact]
    public void ThePathMustBeAtTheLevelTheMessageTypeAddresses()
    {
        // DDATA nói về device còn NBIRTH nói về node bên trên. Publish một cái ở cấp của cái kia tạo ra
        // topic mà subscriber bind được nhưng không có gì nhất quán đến trên đó.
        Should.Throw<ArgumentException>(() => SparkplugTopic.For(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1"),
            SparkplugMessageType.DeviceData));

        Should.Throw<ArgumentException>(() => SparkplugTopic.For(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01"),
            SparkplugMessageType.NodeBirth));
    }

    [Theory]
    // Chữ thường là convention của Unified Namespace cho cùng các máy (docs/scope.md §7.1). Chấp nhận
    // nó ở đây khiến một cycler có hai identity và mọi count được tính trên nửa dữ liệu. Đây là bẫy
    // M1/C02.1 ở tầng thấp hơn.
    [InlineData("spbv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/novavolt-nv1-formation/DDATA/EDGE-F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/ddata/EDGE-F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/form-01-ch-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/edge-F1/FORM-01-CH-0142")]
    // Type device-level không có device, và type node-level lại có một device.
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/NDATA/EDGE-F1/FORM-01-CH-0142")]
    // Shape hoàn toàn không thuộc namespace này.
    [InlineData("spBv1.0/NOVAVOLT-NV1/DDATA/EDGE-F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/HELLO/EDGE-F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01/CH-0142")]
    [InlineData("novavolt/nv1/formation/f1/form-01/ch-0142/measurement")]
    [InlineData("")]
    [InlineData(null)]
    public void SomethingThatIsNotOneOfOurTopicsIsRefused(string? value)
    {
        SparkplugTopic.TryParse(value, out var topic).ShouldBeFalse();
        topic.ShouldBeNull();
    }

    [Fact]
    public void TheHostStateTopicIsRecognisedRatherThanCalledMalformed()
    {
        // Gateway subscribe spBv1.0/# nên sẽ nhận nó. "Not addressed to us" là bình thường;
        // "malformed" đáng alert. Gộp hai trường hợp sẽ dạy mọi người phớt lờ alert.
        const string HostState = "spBv1.0/STATE/nvm-scada-1";

        SparkplugTopic.TryParse(HostState, out _).ShouldBeFalse();
        SparkplugTopic.IsHostState(HostState).ShouldBeTrue();
        SparkplugTopic.IsHostState(DeviceTopic).ShouldBeFalse();
    }

    [Fact]
    public void EveryMessageTypeHasATokenAndReadsBackFromIt()
    {
        foreach (var messageType in Enum.GetValues<SparkplugMessageType>())
        {
            SparkplugMessageTypes.TryParse(messageType.Token(), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(messageType);
        }

        // Case-sensitive, và đây là assertion nói rõ điều đó.
        SparkplugMessageTypes.TryParse("dbirth", out _).ShouldBeFalse();
    }
}
