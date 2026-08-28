using Nvm.Kernel.Identity;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Reading and writing Sparkplug topics, without asking the plant anything.</summary>
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
        // The direction C05 needs: the simulator publishes as the plant, so it starts from a path.
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
        // The round-trip that closes without a factory model. Note what it does not prove: the work
        // cell FORM-01 is gone from the topic and cannot come back from it — only the directory can
        // return the six-segment path, which is what SparkplugTopicResolutionTests checks.
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
        // The group id joins three codes with the character a code may itself contain. Splitting into
        // exactly three with the remainder going last is what keeps that reversible for the one code
        // that is allowed to be compound.
        var written = SparkplugTopic.For(
            EquipmentPath.Parse("NOVAVOLT/NV1/CELL-FINISHING/F1"),
            SparkplugMessageType.NodeData);

        written.Value.ShouldBe("spBv1.0/NOVAVOLT-NV1-CELL-FINISHING/NDATA/EDGE-F1");
        SparkplugTopic.Parse(written.Value).AreaCode.ShouldBe("CELL-FINISHING");
    }

    [Fact]
    public void AnEnterpriseOrSiteCodeWithAHyphenIsRefusedWhereItIsStillVisible()
    {
        // Caught while writing, because while reading it is undetectable: "NOVA-VOLT-NV1-FORMATION"
        // splits into enterprise NOVA, site VOLT, area NV1-FORMATION, which is a perfectly well-formed
        // topic for a plant that does not exist. The failure would surface much later as "this line is
        // not in the model".
        var thrown = Should.Throw<ArgumentException>(() => SparkplugTopic.For(
            EquipmentPath.Parse("NOVA-VOLT/NV1/FORMATION/F1"),
            SparkplugMessageType.NodeData));

        thrown.Message.ShouldContain("NOVA-VOLT");
    }

    [Fact]
    public void ThePathMustBeAtTheLevelTheMessageTypeAddresses()
    {
        // DDATA is about a device and NBIRTH is about the node above it. Publishing one at the other's
        // level produces a topic that subscribers bind to but nothing consistent ever arrives on.
        Should.Throw<ArgumentException>(() => SparkplugTopic.For(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1"),
            SparkplugMessageType.DeviceData));

        Should.Throw<ArgumentException>(() => SparkplugTopic.For(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01"),
            SparkplugMessageType.NodeBirth));
    }

    [Theory]
    // Lower case is the Unified Namespace convention for the same machines (docs/scope.md §7.1).
    // Accepting it here is how one cycler acquires two identities and every count is taken over half
    // its data. This is the M1/C02.1 trap, one layer down.
    [InlineData("spbv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/novavolt-nv1-formation/DDATA/EDGE-F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/ddata/EDGE-F1/FORM-01-CH-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/form-01-ch-0142")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/edge-F1/FORM-01-CH-0142")]
    // A device-level type with no device, and a node-level type with one.
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1")]
    [InlineData("spBv1.0/NOVAVOLT-NV1-FORMATION/NDATA/EDGE-F1/FORM-01-CH-0142")]
    // Shapes that are not this namespace at all.
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
        // The gateway subscribes to spBv1.0/# and will receive this. "Not addressed to us" is routine;
        // "malformed" is worth an alert. Collapsing the two teaches everyone to ignore the alert.
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

        // Case-sensitively, and this is the assertion that says so.
        SparkplugMessageTypes.TryParse("dbirth", out _).ShouldBeFalse();
    }
}
