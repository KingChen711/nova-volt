using System.Collections.Frozen;
using System.Collections.Immutable;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Entities;

/// <summary>Một revision của factory model: cái cây, các site, và một chỉ mục phẳng trên cả hai.</summary>
/// <remarks>
/// <para>
/// Bất biến (immutable). Một thay đổi ở plant tạo ra một snapshot mới ở revision cao hơn thay vì sửa
/// snapshot này, và đó là điều cho phép một câu hỏi về tháng Ba năm ngoái được trả lời bằng đúng model
/// đang có hiệu lực vào tháng Ba năm ngoái.
/// </para>
/// <para>
/// Hai cách nhìn trên cùng một dữ liệu, vì có hai loại câu hỏi rất khác nhau được đặt ra. Đi bộ qua
/// cây trả lời "cái gì nằm dưới area này"; chỉ mục phẳng trả lời "cái gì là
/// <c>NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142</c>" — chính là câu hỏi được đặt ra trên từng
/// message một đến từ sàn nhà máy, và vì vậy nó phải là một lượt tra dictionary chứ không phải một lượt
/// đi bộ qua cây.
/// </para>
/// </remarks>
public sealed class FactoryModelSnapshot
{
    private readonly FrozenDictionary<EquipmentPath, FactoryNode> _index;

    internal FactoryModelSnapshot(
        int revision,
        DateTimeOffset generatedAt,
        FactoryNode root,
        IEnumerable<FactorySite> sites)
    {
        Revision = revision;
        GeneratedAt = generatedAt;
        Root = root;
        Sites = sites.ToImmutableArray();

        // Dùng Frozen thay vì một dictionary thường: được build một lần lúc load, đọc trên mọi message
        // đến từ sàn nhà máy, và không bao giờ bị ghi lại. Nó cũng loại bỏ cách cuối cùng khiến chỉ
        // mục có thể lệch khỏi cái cây — không có mutator nào để chạm tới, dù qua cast hay cách nào khác.
        _index = root.Descend().ToFrozenDictionary(node => node.Path);
    }

    /// <summary>Đây là revision nào của plant. Tăng ngặt theo thời gian.</summary>
    public int Revision { get; }

    /// <summary>Khi nào file nguồn được tạo ra.</summary>
    public DateTimeOffset GeneratedAt { get; }

    /// <summary>Node enterprise, và thông qua nó là cả cái cây.</summary>
    public FactoryNode Root { get; }

    /// <summary>Các plant trong revision này.</summary>
    public ImmutableArray<FactorySite> Sites { get; }

    /// <summary>Cây này giữ bao nhiêu node, tính chung mọi cấp.</summary>
    public int NodeCount => _index.Count;

    /// <summary>Mọi path trong revision này.</summary>
    public ImmutableArray<EquipmentPath> Paths => _index.Keys;

    /// <summary>
    /// Tìm một node theo path của nó, hoặc trả về null khi revision này không chứa path đó.
    /// </summary>
    /// <remarks>
    /// Trả null thay vì ném exception, và sự khác biệt này quan trọng hơn nhìn bề ngoài. Một path
    /// không tồn tại là trường hợp bình thường khi đọc lịch sử: một work cell đã ngừng hoạt động từ
    /// năm ngoái vẫn được nêu tên bởi mọi traceability record được ghi trong lúc nó còn tồn tại. Coi
    /// đó là lỗi sẽ khiến hệ thống từ chối một phần lịch sử hợp lệ ngay lần đầu một máy bị scrap.
    /// </remarks>
    public FactoryNode? Find(EquipmentPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return _index.GetValueOrDefault(path);
    }

    /// <summary>Tìm một node theo path viết dưới dạng chuỗi text.</summary>
    /// <returns>Node đó, hoặc null khi path chưa từng biết đến <b>hoặc sai định dạng</b>.</returns>
    public FactoryNode? Find(string? path) =>
        EquipmentPath.TryParse(path, out var parsed) ? Find(parsed) : null;

    /// <summary>Tìm một plant theo mã code của nó.</summary>
    public FactorySite? FindSite(string? siteId) =>
        Sites.FirstOrDefault(site => string.Equals(site.SiteId, siteId, StringComparison.Ordinal));
}
