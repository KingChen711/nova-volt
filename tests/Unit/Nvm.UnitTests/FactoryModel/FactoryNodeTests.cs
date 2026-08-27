using Nvm.FactoryModel.Entities;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.FactoryModel;

public sealed class FactoryNodeTests
{
    private static readonly EquipmentPath SitePath = EquipmentPath.Parse("NOVAVOLT/NV1");

    private static FactoryNode Leaf(string path, string name) =>
        FactoryNode.Create(EquipmentPath.Parse(path), name);

    [Fact]
    public void Create_Leaf_TakesCodeKindAndSiteFromItsPath()
    {
        // Derived rather than stored, so a node cannot claim to be in one plant while sitting in
        // another's subtree.
        var node = Leaf("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142", "Channel 142");

        node.Code.ShouldBe("FORM-01-CH-0142");
        node.Kind.ShouldBe(FactoryNodeKind.Equipment);
        node.SiteId.ShouldBe("NV1");
    }

    [Fact]
    public void Create_ChildOfAnotherParent_IsRefused()
    {
        // The tree's shape lives in the paths. A node cannot adopt something from elsewhere in the
        // plant, because its path would no longer describe where it actually is.
        var stranger = Leaf("NOVAVOLT/DE1/MODULE", "Module area at Leipzig");

        var thrown = Should.Throw<ArgumentException>(
            () => FactoryNode.Create(SitePath, "Hai Phong", [stranger]));

        thrown.Message.ShouldContain("NOVAVOLT/DE1/MODULE");
    }

    [Fact]
    public void Create_GrandchildPassedAsChild_IsRefused()
    {
        // One segment deeper, not two. Allowing a skipped level would put a work cell where the depth
        // says an area is, and the level is read off the depth.
        var grandchild = Leaf("NOVAVOLT/NV1/FORMATION/F1", "Formation line 1");

        Should.Throw<ArgumentException>(() => FactoryNode.Create(SitePath, "Hai Phong", [grandchild]));
    }

    [Fact]
    public void Create_TwoChildrenWithTheSameCode_IsRefused()
    {
        // Two machines with one code would give the flat lookup two answers for one path, and
        // whichever the index kept would be the one traceability believed.
        var first = Leaf("NOVAVOLT/NV1/FORMATION", "Formation");
        var duplicate = Leaf("NOVAVOLT/NV1/FORMATION", "Formation, again");

        Should.Throw<ArgumentException>(() => FactoryNode.Create(SitePath, "Hai Phong", [first, duplicate]));
    }

    [Fact]
    public void EquipmentUnderASite_IsNotEvenExpressible()
    {
        // The invariant the plan asks for, and the reason there is no table of permitted parent levels.
        // A child of a site is at depth three, and depth three is an area. Naming a channel there does
        // not make it a channel — it makes it an area with an odd name, and the parent check refuses it
        // as soon as the real channel path is used.
        var channel = Leaf("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142", "Channel 142");

        Should.Throw<ArgumentException>(() => FactoryNode.Create(SitePath, "Hai Phong", [channel]));

        EquipmentPath.Parse("NOVAVOLT/NV1").Append("FORM-01-CH-0142").Kind.ShouldBe(FactoryNodeKind.Area);
    }

    [Fact]
    public void SiteId_OnTheEnterpriseNode_IsNullAndOnEveryDescendantIsNot()
    {
        // AGENTS.md K3, checked over a whole subtree rather than one node.
        var tree = BuildSmallTree();

        tree.SiteId.ShouldBeNull();
        tree.Descend().Skip(1).ShouldAllBe(node => node.SiteId != null);
    }

    [Fact]
    public void Descend_VisitsParentsBeforeChildren()
    {
        var tree = BuildSmallTree();

        var kinds = tree.Descend().Select(node => node.Kind).ToArray();

        kinds.ShouldBe([
            FactoryNodeKind.Enterprise,
            FactoryNodeKind.Site,
            FactoryNodeKind.Area,
            FactoryNodeKind.Line,
        ]);
    }

    [Fact]
    public void Create_BlankName_IsRefused()
    {
        Should.Throw<ArgumentException>(() => FactoryNode.Create(SitePath, "  "));
    }

    private static FactoryNode BuildSmallTree()
    {
        var line = Leaf("NOVAVOLT/NV1/FORMATION/F1", "Formation line 1");
        var area = FactoryNode.Create(EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION"), "Formation", [line]);
        var site = FactoryNode.Create(SitePath, "Hai Phong", [area]);

        return FactoryNode.Create(EquipmentPath.Parse("NOVAVOLT"), "NovaVolt Energy", [site]);
    }
}
