using System.Text.Json.Serialization;

namespace Nvm.Foundation;

/// <summary>Cột hệ thống mà nền tự quản lý cho mọi entity và facet.</summary>
public abstract class ModelObject
{
    /// <summary>Khóa kỹ thuật, nền cấp lúc <c>Create</c>.</summary>
    public Guid Id { get; internal set; }

    /// <summary>Giờ UTC lúc ghi lần đầu, lấy từ <see cref="TimeProvider"/> của nền.</summary>
    public DateTimeOffset CreatedOn { get; internal set; }

    /// <summary>Giờ UTC lúc ghi gần nhất.</summary>
    public DateTimeOffset LastUpdatedOn { get; internal set; }

    /// <summary>Phiên bản lạc quan; hai transaction cùng sửa một dòng thì bên ghi sau thất bại.</summary>
    [JsonIgnore]
    public IReadOnlyList<byte> OptimisticVersion => Version ?? [];

    /// <summary><c>0</c> là bản đang sống. Xóa đặt số lớn nhất của bảng + 1, nên mỗi bản đã xóa có một số riêng.</summary>
    public int IsDeleted { get; internal set; }

    [JsonIgnore]
    internal byte[]? Version { get; set; }
}

/// <summary>Entity: một bảng, khóa logic <see cref="NId"/> duy nhất trong các bản chưa xóa.</summary>
public abstract class Entity : ModelObject
{
    /// <summary>Khóa logic do nghiệp vụ đặt. Unique index trên <c>(IsDeleted, NId)</c>.</summary>
    public string NId { get; set; } = string.Empty;
}

/// <summary>Gốc chung của mọi facet, để nền xử lý facet mà không cần biết entity của nó.</summary>
public abstract class FacetBase : ModelObject
{
    /// <summary>Id của entity được mở rộng; cột <c>&lt;Entity&gt;_Id</c>.</summary>
    public Guid EntityId { get; set; }
}

/// <summary>
/// Facet của <typeparamref name="TEntity"/>. Mọi facet của một entity dùng chung một bảng; cột của từng facet là cột
/// sparse, tên <c>&lt;Facet&gt;_&lt;Thuộc tính&gt;</c>.
/// </summary>
public abstract class Facet<TEntity> : FacetBase
    where TEntity : Entity;
