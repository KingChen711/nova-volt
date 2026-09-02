using System.Collections.Immutable;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.FactoryModel;

/// <summary>
/// Factory model là read-only, và các test này ghim chặt điều đó thực sự nghĩa là gì.
/// </summary>
/// <remarks>
/// <para>
/// Mỗi test ở đây từng bắt đầu như một exploit thực sự chống lại hình dạng trước đó. `IReadOnlyList`
/// chỉ nói rằng <i>chính reference này</i> không cung cấp mutator; nó không nói gì về object đứng sau
/// nó. Một caller vẫn có thể giữ lại list mà nó đã truyền vào, hoặc cast property trở lại `IList`, rồi
/// sửa cây sau đó.
/// </para>
/// <para>
/// Cái giá phải trả trên sàn nhà máy: <see cref="FactoryModelSnapshot"/> xây dựng một flat index đúng
/// một lần, lúc load, và mọi message đến từ một máy đều được resolve qua nó. Sửa cây sau thời điểm đó
/// thì hai bên sẽ không bao giờ khớp nhau nữa — bước walk tìm thấy một charging channel mà lookup lại
/// nói là không tồn tại. Không bên nào rõ ràng là sai, không gì ném exception cả, và sự bất đồng này
/// chỉ lộ ra nhiều tháng sau dưới dạng telemetry không gắn được vào bất kỳ thiết bị nào.
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
        // Create kiểm tra những gì được đưa vào rồi mới giữ một bản sao. Nếu giữ collection của caller
        // thay vào đó thì sẽ kiểm tra một thứ nhưng lưu trữ một thứ khác: caller thêm một node chưa
        // được validate ngay khi Create vừa trả về, và các invariant vừa được enforce coi như biến
        // mất.
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
        // Cách cast từng hoạt động được. Nó vẫn compile — ImmutableArray implement IList để có thể
        // được truyền cho code đang mong đợi một IList — nhưng mọi mutator đều từ chối.
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
        // Ghi qua segments trước đây từng khiến Value nói một đằng còn SiteId, Code và Kind nói một
        // nẻo — một path đặt tên cho hai máy khác nhau tùy vào member nào được hỏi. Cast sang array
        // giờ không còn compile được nữa; test này bao phủ con đường qua interface vẫn còn compile
        // được.
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
        // Hai góc nhìn của cùng một revision, và cách duy nhất chúng có thể bất đồng bây giờ là một
        // bug trong cách index được xây dựng — không còn cách nào để sửa một trong hai sau đó nữa.
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
        // Các đảm bảo ở trên chủ yếu được compiler enforce, nghĩa là chúng sẽ âm thầm biến mất vào
        // ngày ai đó nới rộng một trong số này trở lại thành IReadOnlyList để signature gọn hơn. Đây
        // là test phát hiện ra điều đó.
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
