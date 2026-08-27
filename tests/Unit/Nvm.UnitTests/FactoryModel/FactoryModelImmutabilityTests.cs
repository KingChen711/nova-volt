using System.Collections.Immutable;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.FactoryModel;

/// <summary>
/// The factory model is read-only, and these pin what that actually means.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these started as a working exploit against the previous shape. `IReadOnlyList` says
/// only that <i>this reference</i> offers no mutators; it says nothing about the object behind it. A
/// caller could keep the list it passed in, or cast the property back to `IList`, and edit the tree
/// afterwards.
/// </para>
/// <para>
/// What that costs on the floor: <see cref="FactoryModelSnapshot"/> builds a flat index once, at load,
/// and every message arriving from a machine is resolved through it. Edit the tree after that and the
/// two never agree again — the walk finds a charging channel the lookup says does not exist. Neither
/// side is obviously wrong, nothing throws, and the disagreement surfaces months later as telemetry
/// that will not attach to any equipment.
/// </para>
/// </remarks>
public sealed class FactoryModelImmutabilityTests
{
    private static readonly string SeedPath =
        Path.Combine(AppContext.BaseDirectory, "seed", FactoryModelSeed.FileNameFor(1));

    private static FactoryNode Leaf(string path) => FactoryNode.Create(EquipmentPath.Parse(path), "Leaf");

    [Fact]
    public void MutatingTheCollectionPassedToCreate_DoesNotReachTheNode()
    {
        // Create validates what it is given and then keeps a copy. Keeping the caller's collection
        // instead would check one thing and store another: the caller adds an unvalidated node the
        // moment Create returns, and the invariants it just enforced are gone.
        var children = new List<FactoryNode> { Leaf("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01") };
        var line = FactoryNode.Create(EquipmentPath.Parse("NOVAVOLT/NV1/ASSEMBLY/L1"), "Cell line 1", children);

        children.Add(Leaf("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-99"));
        children.Clear();

        line.Children.Length.ShouldBe(1);
        line.Children[0].Code.ShouldBe("STACK-01");
    }

    [Fact]
    public void Children_CannotBeMutatedThroughTheCollectionInterfaces()
    {
        // The cast that used to work. It still compiles — ImmutableArray implements IList so that it
        // can be passed to code expecting one — but every mutator refuses.
        var line = FactoryNode.Create(
            EquipmentPath.Parse("NOVAVOLT/NV1/ASSEMBLY/L1"),
            "Cell line 1",
            new List<FactoryNode> { Leaf("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01") });

        IList<FactoryNode> asList = line.Children;

        Should.Throw<NotSupportedException>(() => asList.Add(Leaf("NOVAVOLT/DE1/PACK/P1/PLOAD-01")));
        Should.Throw<NotSupportedException>(() => asList.Clear());
        Should.Throw<NotSupportedException>(() => asList[0] = Leaf("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-02"));

        line.Children.Length.ShouldBe(1);
    }

    [Fact]
    public void Segments_CannotBeMutatedThroughTheCollectionInterfaces()
    {
        // Writing through the segments used to leave Value saying one thing and SiteId, Code and Kind
        // saying another — one path naming two different machines depending on which member you asked.
        // The array cast no longer compiles at all; this covers the interface route that still does.
        var path = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

        IList<string> asList = path.Segments;

        Should.Throw<NotSupportedException>(() => asList[1] = "DE1");
        Should.Throw<NotSupportedException>(() => asList.Clear());

        path.SiteId.ShouldBe("NV1");
        path.Value.ShouldBe("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");
    }

    [Fact]
    public void Sites_CannotBeMutatedThroughTheCollectionInterfaces()
    {
        var snapshot = FactoryModelSeed.Load(SeedPath);

        IList<FactorySite> asList = snapshot.Sites;

        Should.Throw<NotSupportedException>(() => asList.Clear());
        snapshot.Sites.Length.ShouldBe(2);
    }

    [Fact]
    public void TheTreeAndTheFlatIndex_TellTheSameStory()
    {
        // Two views of one revision, and the only way they can now disagree is a bug in how the index
        // is built — there is no longer a way to edit one of them afterwards.
        var snapshot = FactoryModelSeed.Load(SeedPath);
        var walked = snapshot.Root.Descend().ToArray();

        snapshot.NodeCount.ShouldBe(walked.Length);
        snapshot.Paths.Length.ShouldBe(walked.Length);

        foreach (var node in walked)
        {
            snapshot.Find(node.Path).ShouldBeSameAs(node);
        }

        snapshot.Paths.ShouldBe(walked.Select(node => node.Path), ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(CollectionsOnThePublicSurface))]
    public void EveryCollectionOnThePublicSurface_IsDeclaredImmutable(Type declaringType, string memberName)
    {
        // The guarantees above are mostly enforced by the compiler, which means they vanish silently
        // the day somebody widens one of these back to IReadOnlyList to make a signature tidier. This
        // is the test that notices.
        var propertyType = declaringType.GetProperty(memberName)!.PropertyType;

        propertyType.IsGenericType.ShouldBeTrue($"{declaringType.Name}.{memberName}");
        propertyType.GetGenericTypeDefinition().ShouldBe(
            typeof(ImmutableArray<>),
            $"{declaringType.Name}.{memberName} must stay an ImmutableArray");
    }

    public static TheoryData<Type, string> CollectionsOnThePublicSurface() => new()
    {
        { typeof(EquipmentPath), nameof(EquipmentPath.Segments) },
        { typeof(FactoryNode), nameof(FactoryNode.Children) },
        { typeof(FactoryModelSnapshot), nameof(FactoryModelSnapshot.Sites) },
        { typeof(FactoryModelSnapshot), nameof(FactoryModelSnapshot.Paths) },
        { typeof(IFactoryModelCatalog), nameof(IFactoryModelCatalog.Revisions) },
    };
}
