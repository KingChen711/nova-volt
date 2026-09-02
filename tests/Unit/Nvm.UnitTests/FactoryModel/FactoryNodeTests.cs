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
        // Được derive thay vì lưu trữ, nên một node không thể tự nhận đang ở một plant trong khi thực
        // ra đang nằm trong subtree của plant khác.
        var node = Leaf("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142", "Channel 142");

        node.Code.ShouldBe("FORM-01-CH-0142");
        node.Kind.ShouldBe(FactoryNodeKind.Equipment);
        node.SiteId.ShouldBe("NV1");
    }

    [Fact]
    public void Create_ChildOfAnotherParent_IsRefused()
    {
        // Hình dạng của cây sống trong các path. Một node không thể nhận nuôi một thứ gì đó từ nơi
        // khác trong plant, vì path của nó khi đó sẽ không còn mô tả đúng nơi nó thực sự đang ở.
        var stranger = Leaf("NOVAVOLT/DE1/MODULE", "Module area at Leipzig");

        var thrown = Should.Throw<ArgumentException>(
            () => FactoryNode.Create(SitePath, "Hai Phong", [stranger]));

        thrown.Message.ShouldContain("NOVAVOLT/DE1/MODULE");
    }

    [Fact]
    public void Create_GrandchildPassedAsChild_IsRefused()
    {
        // Sâu hơn đúng một segment, không phải hai. Cho phép bỏ qua một level sẽ đặt một work cell vào
        // chỗ mà depth nói là một area, trong khi level lại được đọc ra từ depth.
        var grandchild = Leaf("NOVAVOLT/NV1/FORMATION/F1", "Formation line 1");

        Should.Throw<ArgumentException>(() => FactoryNode.Create(SitePath, "Hai Phong", [grandchild]));
    }

    [Fact]
    public void Create_TwoChildrenWithTheSameCode_IsRefused()
    {
        // Hai máy dùng chung một code sẽ khiến flat lookup có hai câu trả lời cho cùng một path, và
        // bất kỳ cái nào index giữ lại sẽ là cái mà traceability tin theo.
        var first = Leaf("NOVAVOLT/NV1/FORMATION", "Formation");
        var duplicate = Leaf("NOVAVOLT/NV1/FORMATION", "Formation, again");

        Should.Throw<ArgumentException>(() => FactoryNode.Create(SitePath, "Hai Phong", [first, duplicate]));
    }

    [Fact]
    public void EquipmentUnderASite_IsNotEvenExpressible()
    {
        // Invariant mà plan yêu cầu, và cũng là lý do không có một bảng liệt kê các parent level được
        // phép. Một con của một site nằm ở depth ba, và depth ba là một area. Đặt tên một channel ở đó
        // không biến nó thành một channel — nó biến thành một area với cái tên kỳ quặc, và parent
        // check sẽ từ chối ngay khi path channel thật được dùng.
        var channel = Leaf("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142", "Channel 142");

        Should.Throw<ArgumentException>(() => FactoryNode.Create(SitePath, "Hai Phong", [channel]));

        EquipmentPath.Parse("NOVAVOLT/NV1").Append("FORM-01-CH-0142").Kind.ShouldBe(FactoryNodeKind.Area);
    }

    [Fact]
    public void SiteId_OnTheEnterpriseNode_IsNullAndOnEveryDescendantIsNot()
    {
        // AGENTS.md K3, được kiểm tra trên toàn bộ subtree thay vì chỉ một node.
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
