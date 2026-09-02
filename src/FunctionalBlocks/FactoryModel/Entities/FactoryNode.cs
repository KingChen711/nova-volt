using System.Collections.Immutable;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Entities;

/// <summary>Một node trong cây ISA-95 của một site, cùng với mọi thứ nằm bên dưới nó.</summary>
/// <remarks>
/// <para>
/// Hình dạng của cây được mang bởi <see cref="EquipmentPath"/> thay vì bởi một tham chiếu tới parent
/// hay một bảng liệt kê các cấp parent được phép. Đó là điều khiến "equipment treo thẳng dưới một
/// site" trở thành thứ không thể biểu diễn được chứ không chỉ đơn thuần bị cấm: path của một child mở
/// rộng path của parent thêm đúng một segment, cấp độ được đọc ra từ độ sâu, nên một child của site
/// chỉ có thể là một area và không thể là gì khác.
/// </para>
/// <para>
/// Chỉ đọc (read-only). Thay đổi plant nghĩa là activate một revision mới, không phải sửa một node
/// tại chỗ — cùng lý do khiến event store là append-only. Các traceability record được ghi từ năm
/// ngoái trỏ tới những path có thể không còn tồn tại nữa, và chúng vẫn phải resolve về đúng thứ chúng
/// từng ám chỉ vào lúc đó.
/// </para>
/// </remarks>
public sealed record FactoryNode
{
    private FactoryNode(EquipmentPath path, string name, ImmutableArray<FactoryNode> children)
    {
        Path = path;
        Name = name;
        Children = children;
    }

    /// <summary>Node này nằm ở đâu trong phân cấp.</summary>
    public EquipmentPath Path { get; }

    /// <summary>Tên mà người ta gọi nó, dùng cho màn hình và báo cáo. Không bao giờ là một định danh.</summary>
    public string Name { get; }

    /// <summary>Các node ở một cấp thấp hơn, theo đúng thứ tự model khai báo chúng.</summary>
    /// <remarks>
    /// Dùng <see cref="ImmutableArray{T}"/>, vì <c>IReadOnlyList</c> chỉ là một lời hứa về một tham
    /// chiếu chứ không phải về đối tượng đứng sau nó: trả về <c>List</c> của caller thông qua nó thì
    /// caller vẫn có thể thêm vào, hoặc ai đó cũng có thể cast ngược lại và làm điều tương tự. Một node
    /// được thêm vào hay mất đi sau khi <see cref="FactoryModelSnapshot"/> đã build xong chỉ mục phẳng
    /// của nó sẽ đẩy cây và chỉ mục vào tình trạng bất đồng vĩnh viễn — lượt đi bộ tìm thấy một máy mà
    /// lượt tra cứu (lookup) lại phủ nhận sự tồn tại — và lookup chính là thứ mọi message từ sàn nhà
    /// máy sử dụng.
    /// </remarks>
    public ImmutableArray<FactoryNode> Children { get; }

    /// <summary>Mã code riêng của node, segment cuối cùng trong path của nó.</summary>
    public string Code => Path.Code;

    /// <summary>Đây là cấp nào trong phân cấp.</summary>
    public FactoryNodeKind Kind => Path.Kind;

    /// <summary>
    /// Plant mà node này thuộc về, hoặc null đối với chính enterprise.
    /// </summary>
    /// <remarks>
    /// Được suy ra từ path thay vì lưu trữ riêng, để một node không thể tự nhận thuộc về một plant
    /// trong khi thực chất nằm trong subtree của plant khác. AGENTS.md K3 yêu cầu mọi record phải mang
    /// theo một site; enterprise là cấp duy nhất mà câu hỏi đó không có câu trả lời, và nó được trả lời
    /// bằng null thay vì bằng một chuỗi rỗng — thứ vẫn có thể sort và so sánh như một site code thật.
    /// </remarks>
    public string? SiteId => Path.SiteId;

    /// <summary>Xây một node và kiểm tra rằng các children của nó thực sự là children của nó.</summary>
    /// <param name="path">Node nằm ở đâu.</param>
    /// <param name="name">Tên hiển thị.</param>
    /// <param name="children">Các node ở cấp thấp hơn một bậc, hoặc null nếu là một leaf. Được sao chép, không giữ tham chiếu gốc.</param>
    /// <exception cref="ArgumentException">
    /// Path của một child không phải là path của node này mở rộng thêm đúng một segment, hoặc hai
    /// children dùng chung một code.
    /// </exception>
    public static FactoryNode Create(EquipmentPath path, string name, IEnumerable<FactoryNode>? children = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Sao chép ngay tại đây, trước khi kiểm tra bất cứ điều gì. Validate collection của caller rồi
        // lại lưu chính collection đó sẽ kiểm tra một thứ nhưng giữ lại một thứ khác: caller vẫn đang
        // giữ nó và có thể thêm một node chưa được validate ngay khi hàm này return.
        var declared = children is null ? [] : children.ToImmutableArray();

        foreach (var child in declared)
        {
            if (child.Path.Parent != path)
            {
                throw new ArgumentException(
                    $"'{child.Path}' is not a child of '{path}'. A child's path is its parent's plus one segment.",
                    nameof(children));
            }
        }

        // Hai máy dùng cùng một code trong một cell sẽ khiến flat lookup có hai câu trả lời cho cùng
        // một path, và index tình cờ giữ lại cái nào thì traceability sẽ tin vào đúng cái đó.
        var codes = declared.Select(child => child.Code).ToArray();

        if (codes.Length != codes.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException($"'{path}' has two children with the same code.", nameof(children));
        }

        return new FactoryNode(path, name, declared);
    }

    /// <summary>Đi bộ qua node này và mọi thứ bên dưới nó, parent trước rồi mới đến children.</summary>
    public IEnumerable<FactoryNode> Descend()
    {
        yield return this;

        foreach (var descendant in Children.SelectMany(child => child.Descend()))
        {
            yield return descendant;
        }
    }
}
