using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.FactoryModel;

public sealed class FactoryModelSeedTests
{
    // Cụ thể là revision 1, và là file thật chứ không phải một fixture đã bị cắt gọn. Các con số đếm
    // bên dưới thuộc về đúng document đó và không thuộc về bất kỳ cái nào khác: revision 2 và 3 tồn
    // tại song song với nó và cố tình có con số khác. Sửa r1 sẽ khiến các test này đỏ, và đó chính là
    // mục đích — một thay đổi lên một revision đã publish phải là điều không thể xảy ra một cách vô
    // tình, vì đó không phải là việc một plant được phép làm.
    private static readonly FactoryModelSnapshot Snapshot =
        FactoryModelSeed.Load(Path.Combine(AppContext.BaseDirectory, "seed", FactoryModelSeed.FileNameFor(1)));

    [Fact]
    public void Seed_HasARevisionAndAGenerationTimestamp()
    {
        Snapshot.Revision.ShouldBeGreaterThanOrEqualTo(1);
        Snapshot.GeneratedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(FactoryNodeKind.Enterprise, 1)]
    [InlineData(FactoryNodeKind.Site, 2)]
    [InlineData(FactoryNodeKind.Area, 10)]
    [InlineData(FactoryNodeKind.Line, 11)]
    [InlineData(FactoryNodeKind.WorkCell, 13)]
    [InlineData(FactoryNodeKind.Equipment, 4)]
    public void Seed_HasTheExpectedNumberOfNodesAtEachLevel(FactoryNodeKind kind, int expected)
    {
        var actual = Snapshot.Root.Descend().Count(node => node.Kind == kind);

        actual.ShouldBe(expected, $"the seed changed; update this number deliberately, do not relax the test");
    }

    [Fact]
    public void Seed_TotalNodeCountMatchesTheIndex()
    {
        // Flat index được xây dựng từ bước walk, nên một sự sai lệch sẽ nghĩa là hai node đã bị gộp
        // vào cùng một dictionary entry — đúng là loại lỗi duplicate-path mà index không được phép
        // che giấu.
        Snapshot.NodeCount.ShouldBe(41);
        Snapshot.Paths.Length.ShouldBe(Snapshot.Root.Descend().Count());
    }

    [Fact]
    public void Seed_HasNoDuplicatePaths()
    {
        var paths = Snapshot.Root.Descend().Select(node => node.Path.Value).ToArray();

        paths.Distinct(StringComparer.Ordinal).Count().ShouldBe(paths.Length);
    }

    [Fact]
    public void Seed_EveryNodeBelowTheEnterpriseCarriesASite()
    {
        // AGENTS.md K3, trên toàn bộ cây thay vì chỉ một node. Một sự rò rỉ qua ranh giới site là một
        // lỗi bảo mật, và nó bắt đầu từ một record không biết mình đến từ plant nào.
        var withoutSite = Snapshot.Root.Descend()
            .Where(node => node.Kind != FactoryNodeKind.Enterprise && string.IsNullOrEmpty(node.SiteId))
            .ToArray();

        withoutSite.ShouldBeEmpty();
    }

    [Fact]
    public void Seed_HasBothPlantsWithTheirTimeZones()
    {
        // DE1 có mặt trong model là cố ý: Europe/Berlin áp dụng daylight saving, khiến shift C của nó
        // dài chín giờ một lần mỗi năm và bảy giờ một lần mỗi năm (docs/scope.md §2.3). M3 phải xử lý
        // đúng chuyện đó, và nó không thể làm được nếu plant này không có mặt để mà sai trên đó.
        Snapshot.Sites.Select(site => site.SiteId).ShouldBe(["NV1", "DE1"], ignoreOrder: true);
        Snapshot.FindSite("NV1")!.TimeZoneId.ShouldBe("Asia/Ho_Chi_Minh");
        Snapshot.FindSite("DE1")!.TimeZoneId.ShouldBe("Europe/Berlin");
    }

    [Fact]
    public void Seed_ContainsTheStackerNamedInTheEventContract()
    {
        // docs/scope.md §7.4 dùng chính path này làm equipmentId của một serialization event. Nếu ví
        // dụ trong tài liệu không resolve được trên đúng plant đã tài liệu hóa, thì một trong hai tài
        // liệu đang mô tả một hệ thống không tồn tại.
        var stacker = Snapshot.Find("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-02");

        stacker.ShouldNotBeNull();
        stacker.Kind.ShouldBe(FactoryNodeKind.WorkCell);
        stacker.SiteId.ShouldBe("NV1");
    }

    [Fact]
    public void Find_ByPath_ReturnsTheNodeAtEveryLevel()
    {
        Snapshot.Find("NOVAVOLT")!.Kind.ShouldBe(FactoryNodeKind.Enterprise);
        Snapshot.Find("NOVAVOLT/NV1")!.Kind.ShouldBe(FactoryNodeKind.Site);
        Snapshot.Find("NOVAVOLT/NV1/FORMATION")!.Kind.ShouldBe(FactoryNodeKind.Area);
        Snapshot.Find("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001")!.Kind
            .ShouldBe(FactoryNodeKind.Equipment);
    }

    [Fact]
    public void Find_PathThatIsNotInThisRevision_ReturnsNullRatherThanThrowing()
    {
        // Trường hợp bình thường khi đọc lịch sử. Một channel đã decommission năm ngoái vẫn được nêu
        // tên trong mọi traceability record được ghi trong lúc nó còn tồn tại, và đó là lịch sử hợp lệ
        // chứ không phải dữ liệu hỏng.
        Snapshot.Find("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-9999").ShouldBeNull();
    }

    [Fact]
    public void Find_MalformedPath_ReturnsNullRatherThanThrowing()
    {
        Snapshot.Find("novavolt/nv1").ShouldBeNull();
        Snapshot.Find("NOVAVOLT//NV1").ShouldBeNull();
        Snapshot.Find((string?)null).ShouldBeNull();
    }

    [Fact]
    public void Find_ByPathObject_MatchesFindByText()
    {
        var text = "NOVAVOLT/NV1/FORMATION/F1/FORM-01";

        Snapshot.Find(EquipmentPath.Parse(text)).ShouldBe(Snapshot.Find(text));
    }

    [Theory]
    [InlineData("{}", "no enterprise at all")]
    [InlineData("{\"revision\":0,\"generatedAt\":\"2026-08-26T09:00:00+00:00\",\"enterprise\":{\"code\":\"NOVAVOLT\",\"name\":\"N\"}}", "revision below 1")]
    [InlineData("{\"revision\":1,\"generatedAt\":\"2026-08-26T09:00:00+00:00\",\"enterprise\":{\"code\":\"novavolt\",\"name\":\"N\"}}", "lower-case enterprise code")]
    public void Parse_MalformedSeed_ThrowsRatherThanLoadingHalfAPlant(string json, string reason)
    {
        // Cố tình fatal ngay lúc startup. Một service chạy trên một factory model đọc dở dang sẽ
        // resolve được một số equipment path và âm thầm fail những cái khác, và các lỗ hổng đó sẽ
        // trông như dữ liệu bị thiếu chứ không phải một file hỏng.
        Should.Throw<FactoryModelSeedException>(() => FactoryModelSeed.Parse(json), reason);
    }

    [Fact]
    public void Parse_TimeZoneOnSomethingThatIsNotASite_IsRejected()
    {
        var json = """
            {
              "revision": 1,
              "generatedAt": "2026-08-26T09:00:00+00:00",
              "enterprise": {
                "code": "NOVAVOLT",
                "name": "NovaVolt",
                "timeZoneId": "Asia/Ho_Chi_Minh",
                "children": []
              }
            }
            """;

        var thrown = Should.Throw<FactoryModelSeedException>(() => FactoryModelSeed.Parse(json));

        thrown.Message.ShouldContain("timeZoneId");
    }

    [Fact]
    public void Parse_SiteWithoutATimeZone_IsRejected()
    {
        // Thiếu nó, M3 không có cách nào quyết định một ca đêm thuộc về ngày sản xuất nào, và lỗi này
        // sẽ chỉ xuất hiện nhiều tháng sau dưới dạng các báo cáo không khớp với shift log.
        var json = """
            {
              "revision": 1,
              "generatedAt": "2026-08-26T09:00:00+00:00",
              "enterprise": {
                "code": "NOVAVOLT",
                "name": "NovaVolt",
                "children": [ { "code": "NV1", "name": "Hai Phong" } ]
              }
            }
            """;

        Should.Throw<FactoryModelSeedException>(() => FactoryModelSeed.Parse(json));
    }

    [Fact]
    public void Parse_NodeTooDeepForTheHierarchy_IsRejected()
    {
        // Một sensor bên trong một charging channel là một thuộc tính của thiết bị, không phải một
        // level thứ bảy. Cho phép điều đó sẽ phá vỡ quy tắc rằng depth cho biết một path đang đặt tên
        // cho cái gì.
        var json = """
            {
              "revision": 1,
              "generatedAt": "2026-08-26T09:00:00+00:00",
              "enterprise": {
                "code": "NOVAVOLT", "name": "NovaVolt",
                "children": [ { "code": "NV1", "name": "Hai Phong", "timeZoneId": "Asia/Ho_Chi_Minh",
                  "children": [ { "code": "FORMATION", "name": "Formation",
                    "children": [ { "code": "F1", "name": "Line 1",
                      "children": [ { "code": "FORM-01", "name": "Cycler",
                        "children": [ { "code": "FORM-01-CH-0001", "name": "Channel",
                          "children": [ { "code": "SENSOR-3", "name": "Thermocouple" } ] } ] } ] } ] } ] } ]
              }
            }
            """;

        var thrown = Should.Throw<FactoryModelSeedException>(() => FactoryModelSeed.Parse(json));

        thrown.Message.ShouldContain("SENSOR-3");
    }
}
