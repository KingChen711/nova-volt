namespace Nvm.ArchitectureTests;

public sealed class LayerDependencyTests
{
    [Fact]
    public void Every_project_in_src_belongs_to_a_known_layer()
    {
        var root = LayerRules.RepoRoot();
        var layers = Directory.GetDirectories(Path.Combine(root, "src")).Select(Path.GetFileName);

        layers.ShouldAllBe(l => LayerRules.Allowed.ContainsKey(l!));
    }

    [Fact]
    public void Project_references_follow_layer_direction()
    {
        var violations = LayerRules.Violations(LayerRules.Edges(LayerRules.RepoRoot())).ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Rule_rejects_a_reference_against_the_direction()
    {
        // Đối chứng dương: nếu luật không bắt được cạnh sai chiều thì test trên xanh vô nghĩa.
        var edges = new[] { ("Product", "Nvm.Product", "Project", "Nvm.Project") };

        LayerRules.Violations(edges).ShouldHaveSingleItem();
    }

    [Fact]
    public void Graph_is_not_empty()
    {
        // Thư mục src đổi tên mà đường dẫn quét không đổi theo thì đồ thị rỗng và mọi luật xanh giả.
        LayerRules.Edges(LayerRules.RepoRoot()).ShouldNotBeEmpty();
    }
}
