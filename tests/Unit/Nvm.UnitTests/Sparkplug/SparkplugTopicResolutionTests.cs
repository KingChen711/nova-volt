using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug.Topics;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Đổi topic thành vị trí trong nhà máy, đối chiếu với model nhà máy đang chạy.</summary>
/// <remarks>
/// Seed thật, không phải fixture tree — cùng document mà host load. Device resolve được ở đây vì NV1
/// thực sự có nó, còn device không resolve được thì thiếu vì lý do ai đó có thể tra cứu.
/// </remarks>
public sealed class SparkplugTopicResolutionTests
{
    private static readonly string SeedDirectory = Path.Combine(AppContext.BaseDirectory, "seed");

    [Fact]
    public void ADeviceInsideAWorkCellResolvesToTheSixSegmentPath()
    {
        // Nửa round-trip mà topic không tự làm được. FORM-01 không xuất hiện trong topic; lý do duy
        // nhất kết quả có sáu segment là model đã được hỏi.
        Resolve("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0001")
            .ShouldNotBeNull()
            .Value
            .ShouldBe("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");
    }

    [Fact]
    public void ADeviceThatIsItselfAWorkCellResolvesToTheFiveSegmentPath()
    {
        // STACK-01 treo trực tiếp dưới line L1. Topic có shape giống hệt topic trên, nên depth không
        // thể là rule trong parser.
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
        // K3, và hình dạng nó có ở ingestion. DE1 là nhà máy thật trong document nhưng chưa được rollout
        // ở đây, nên topic nói về nó không có kết quả — không đoán từ tree của NV1, cũng không lấy
        // document mới nhất trên kệ.
        var directory = DirectoryWith(("NV1", 1));

        SparkplugTopic
            .Parse("spBv1.0/NOVAVOLT-DE1-PACK/DBIRTH/EDGE-P1/PLOAD-01")
            .ResolveEquipmentPath(directory)
            .ShouldBeNull();

        // Cùng topic với directory đã activate DE1 thì resolve được, nên null ở trên nói về activation
        // chứ không phải topic không đọc được.
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
        // NV1 và DE1 đều có MLOAD-01 trên line M1. Code index toàn nhà máy sẽ trả về cái được lưu sau
        // cùng, và một nửa history của module loader sẽ bị ghi dưới nhà máy sai.
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
        // Revision 1 của NV1 có channel 0001 đến 0004. Cycler report 0142 hoặc là nhà máy đã mở rộng
        // mà không có revision mới, hoặc topic do ai đó gõ. Cả hai đều phải từ chối.
        Resolve("spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142").ShouldBeNull();
    }

    [Fact]
    public void ADeviceAddedInALaterRevisionAppearsOnlyOnceThePlantIsRunningIt()
    {
        // Revision 2 thêm channel 0005 đến 0008 (ADR-024). Cùng topic resolve khác nhau giữa revision
        // là staged rollout hoạt động đúng, không phải inconsistency: NV1 trả lời theo document NV1 đã
        // activate.
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
        // Phiên bản node-level của cùng sự từ chối. NV1 không có line F9.
        Resolve("spBv1.0/NOVAVOLT-NV1-FORMATION/NBIRTH/EDGE-F9").ShouldBeNull();
    }

    [Fact]
    public void ADeviceOnTheWrongLineResolvesToNothing()
    {
        // FORM-01-CH-0001 tồn tại trên F1. Report dưới AGING/A1 thì hoặc máy đã di chuyển mà không có
        // revision mới, hoặc gateway address sai; vẫn resolve nó sẽ ghi formation reading dưới aging rack.
        Resolve("spBv1.0/NOVAVOLT-NV1-AGING/DDATA/EDGE-A1/FORM-01-CH-0001").ShouldBeNull();
    }

    [Fact]
    public void WritingATopicFromAResolvedPathAndResolvingItAgainReturnsTheSamePath()
    {
        // Full round-trip, và là round-trip duy nhất khép kín cho path sáu segment: path → topic mất
        // work cell, topic → path khôi phục nó từ model.
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
