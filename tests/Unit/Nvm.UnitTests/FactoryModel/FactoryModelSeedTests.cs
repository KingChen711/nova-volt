using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.FactoryModel;

public sealed class FactoryModelSeedTests
{
    // Revision 1 specifically, and the real file rather than a trimmed fixture. The counts below
    // belong to that document and to no other: revisions 2 and 3 exist next to it and have different
    // numbers on purpose. Editing r1 turns these red, and that is the point — a change to a published
    // revision should be impossible to make by accident, because it is not a thing a plant may do.
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
        // The flat index is built from the walk, so a mismatch would mean two nodes collapsed into one
        // dictionary entry — which is exactly the duplicate-path failure the index must not hide.
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
        // AGENTS.md K3, over the whole tree rather than one node. A leak across the site boundary is a
        // security defect, and it starts with a record that does not know which plant it came from.
        var withoutSite = Snapshot.Root.Descend()
            .Where(node => node.Kind != FactoryNodeKind.Enterprise && string.IsNullOrEmpty(node.SiteId))
            .ToArray();

        withoutSite.ShouldBeEmpty();
    }

    [Fact]
    public void Seed_HasBothPlantsWithTheirTimeZones()
    {
        // DE1 is in the model on purpose: Europe/Berlin observes daylight saving, which makes its
        // shift C nine hours long once a year and seven once a year (docs/scope.md §2.3). M3 has to
        // get that right, and it cannot if the plant is not there to get it wrong on.
        Snapshot.Sites.Select(site => site.SiteId).ShouldBe(["NV1", "DE1"], ignoreOrder: true);
        Snapshot.FindSite("NV1")!.TimeZoneId.ShouldBe("Asia/Ho_Chi_Minh");
        Snapshot.FindSite("DE1")!.TimeZoneId.ShouldBe("Europe/Berlin");
    }

    [Fact]
    public void Seed_ContainsTheStackerNamedInTheEventContract()
    {
        // docs/scope.md §7.4 uses this exact path as the equipmentId of a serialization event. If the
        // documented example does not resolve against the documented plant, one of the two documents
        // is describing a system that does not exist.
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
        // The normal case when reading history. A channel decommissioned last year is still named by
        // every traceability record written while it existed, and that is valid history rather than
        // corrupt data.
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
        // Fatal at startup on purpose. A service running on a partially read factory model would
        // resolve some equipment paths and silently fail others, and the gaps would look like missing
        // data rather than a bad file.
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
        // Without it, M3 has no way to decide which production day a night shift belongs to, and the
        // failure would appear months later as reports that disagree with the shift log.
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
        // A sensor inside a charging channel is an attribute of the equipment, not a seventh level.
        // Allowing one would break the rule that depth tells you what a path names.
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
