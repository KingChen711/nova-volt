using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Turning a topic into a place in the plant, against the model the plant is running.</summary>
/// <remarks>
/// The real seed, not a fixture tree — the same documents the host loads. A device that resolves here
/// resolves because NV1 genuinely has it, and one that does not is missing for a reason somebody could
/// look up.
/// </remarks>
public sealed class SparkplugTopicResolutionTests
{
    private static readonly string SeedDirectory = Path.Combine(AppContext.BaseDirectory, "seed");

    [Fact]
    public void ADeviceInsideAWorkCellResolvesToTheSixSegmentPath()
    {
        // The half of the round-trip a topic cannot do alone. FORM-01 appears nowhere in the topic;
        // the only reason the answer has six segments is that the model was asked.
        Resolve("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0001")
            .ShouldNotBeNull()
            .Value
            .ShouldBe("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");
    }

    [Fact]
    public void ADeviceThatIsItselfAWorkCellResolvesToTheFiveSegmentPath()
    {
        // STACK-01 hangs straight off line L1. The topic looks identical in shape to the one above,
        // which is exactly why the depth cannot be a rule in the parser.
        Resolve("spBv1.0/NOVAVOLT-NV1-ASSEMBLY/DBIRTH/EDGE-L1/STACK-01")
            .ShouldNotBeNull()
            .Value
            .ShouldBe("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01");
    }

    [Fact]
    public void ANodeLevelTopicResolvesToItsLine()
    {
        Resolve("spBv1.0/NOVAVOLT-NV1-FORMATION/NBIRTH/EDGE-F1")
            .ShouldNotBeNull()
            .Value
            .ShouldBe("NOVAVOLT/NV1/FORMATION/F1");
    }

    [Fact]
    public void APlantThatHasActivatedNothingResolvesNothing()
    {
        // K3, and the shape it takes at ingestion. DE1 is a real plant in the document and has not been
        // rolled out here, so a topic quoting it gets no answer — not a guess drawn from NV1's tree,
        // and not the newest document on the shelf either.
        var directory = DirectoryWith(("NV1", 1));

        SparkplugTopic
            .Parse("spBv1.0/NOVAVOLT-DE1-PACK/DBIRTH/EDGE-P1/PLOAD-01")
            .ResolveEquipmentPath(directory)
            .ShouldBeNull();

        // The same topic against a directory where DE1 has been activated does resolve, so the null
        // above is about activation and not about the topic being unreadable.
        SparkplugTopic
            .Parse("spBv1.0/NOVAVOLT-DE1-PACK/DBIRTH/EDGE-P1/PLOAD-01")
            .ResolveEquipmentPath(DirectoryWith(("NV1", 1), ("DE1", 1)))
            .ShouldNotBeNull()
            .Value
            .ShouldBe("NOVAVOLT/DE1/PACK/P1/PLOAD-01");
    }

    [Fact]
    public void CodesThatRepeatAcrossPlantsResolveWithinTheirOwnPlant()
    {
        // NV1 and DE1 both have MLOAD-01 on line M1. A plant-wide code index would answer with
        // whichever it happened to store last, and half the module loader's history would be filed
        // under the wrong factory.
        var directory = DirectoryWith(("NV1", 1), ("DE1", 1));

        SparkplugTopic.Parse("spBv1.0/NOVAVOLT-NV1-MODULE/DDATA/EDGE-M1/MLOAD-01")
            .ResolveEquipmentPath(directory)
            .ShouldNotBeNull()
            .Value
            .ShouldBe("NOVAVOLT/NV1/MODULE/M1/MLOAD-01");

        SparkplugTopic.Parse("spBv1.0/NOVAVOLT-DE1-MODULE/DDATA/EDGE-M1/MLOAD-01")
            .ResolveEquipmentPath(directory)
            .ShouldNotBeNull()
            .Value
            .ShouldBe("NOVAVOLT/DE1/MODULE/M1/MLOAD-01");
    }

    [Fact]
    public void ADeviceThePlantDoesNotHaveResolvesToNothing()
    {
        // Revision 1 of NV1 has channels 0001 to 0004. A cycler reporting 0142 is either a plant that
        // has been extended without a new revision, or a topic somebody typed. Both are refusals.
        Resolve("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142").ShouldBeNull();
    }

    [Fact]
    public void ADeviceAddedInALaterRevisionAppearsOnlyOnceThePlantIsRunningIt()
    {
        // Revision 2 adds channels 0005 to 0008 (ADR-024). The same topic resolving differently at
        // different revisions is staged rollout working, not an inconsistency: NV1 answers for the
        // document NV1 has activated.
        const string Topic = "spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0005";

        SparkplugTopic.Parse(Topic).ResolveEquipmentPath(DirectoryWith(("NV1", 1))).ShouldBeNull();

        SparkplugTopic.Parse(Topic)
            .ResolveEquipmentPath(DirectoryWith(("NV1", 2)))
            .ShouldNotBeNull()
            .Value
            .ShouldBe("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0005");
    }

    [Fact]
    public void ALineThePlantDoesNotHaveResolvesToNothing()
    {
        // The node-level version of the same refusal. NV1 has no line F9.
        Resolve("spBv1.0/NOVAVOLT-NV1-FORMATION/NBIRTH/EDGE-F9").ShouldBeNull();
    }

    [Fact]
    public void ADeviceOnTheWrongLineResolvesToNothing()
    {
        // FORM-01-CH-0001 exists, on F1. Reported under AGING/A1 it is either a machine that moved
        // without a new revision or a mis-addressed gateway; resolving it anyway would file formation
        // readings under an aging rack.
        Resolve("spBv1.0/NOVAVOLT-NV1-AGING/DDATA/EDGE-A1/FORM-01-CH-0001").ShouldBeNull();
    }

    [Fact]
    public void WritingATopicFromAResolvedPathAndResolvingItAgainReturnsTheSamePath()
    {
        // The full round-trip, and the only one that closes for a six-segment path: path → topic loses
        // the work cell, topic → path recovers it from the model.
        var directory = DirectoryWith(("NV1", 1));
        var original = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0003");

        var topic = SparkplugTopic.For(original, SparkplugMessageType.DeviceData);

        topic.Value.ShouldNotContain("FORM-01/");
        SparkplugTopic.Parse(topic.Value).ResolveEquipmentPath(directory).ShouldBe(original);
    }

    private static EquipmentPath? Resolve(string topic) =>
        SparkplugTopic.Parse(topic).ResolveEquipmentPath(DirectoryWith(("NV1", 1)));

    private static FactoryModelEquipmentDirectory DirectoryWith(params (string SiteId, int Revision)[] activations)
    {
        var catalog = FactoryModelSeed.LoadCatalog(SeedDirectory);
        var active = new InMemoryActiveFactoryModel();

        foreach (var (siteId, revision) in activations)
        {
            var site = catalog.Find(revision)!.Sites.Single(candidate => candidate.SiteId == siteId);

            active.TryActivate(new ActiveFactoryModelRevision(revision, site), null).ShouldBeTrue();
        }

        return new FactoryModelEquipmentDirectory(active);
    }
}
