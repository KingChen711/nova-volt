namespace Nvm.Foundation;

/// <summary>Ba domain dữ liệu của nền. Transaction, <c>Query</c> và pre-check đều bị giới hạn trong một domain.</summary>
public enum ModelDomain
{
    MasterData = 1,
    OperationalData = 2,
    ReferenceData = 3,
}

/// <summary>Đánh dấu một lớp rỗng là functional block: một gói model thuộc đúng một domain.</summary>
/// <remarks>
/// Thay cho file khai báo model của nền thật. Entity, command, pre-check… trỏ về block bằng attribute generic,
/// nên domain của chúng là domain của block, không khai lại ở từng chỗ.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FunctionalBlockAttribute(string name, ModelDomain domain) : Attribute
{
    public string Name { get; } = name;

    public ModelDomain Domain { get; } = domain;
}

/// <summary>Entity của <typeparamref name="TBlock"/>. Lớp phải kế thừa <see cref="Entity"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EntityAttribute<TBlock> : Attribute
    where TBlock : class;

/// <summary>Facet của <typeparamref name="TBlock"/>: thêm thuộc tính cho entity có sẵn mà không sửa nó.</summary>
/// <remarks>Lớp phải kế thừa <see cref="Facet{TEntity}"/>; entity được mở rộng có thể thuộc block khác.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FacetAttribute<TBlock> : Attribute
    where TBlock : class;

/// <summary>Command của <typeparamref name="TBlock"/>. Handler của nó implement <see cref="ICommandHandler{TCommand,TResponse}"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandAttribute<TBlock> : Attribute
    where TBlock : class;

/// <summary>Composite command của một app: gọi command của FB, mỗi lời gọi một transaction riêng.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AppCommandAttribute(string app) : Attribute
{
    public string App { get; } = app;
}

/// <summary>Pre-check của <typeparamref name="TBlock"/>; tên là tên lớp. Chỉ chạy ở app đã bật nó.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PreCheckAttribute<TBlock> : Attribute
    where TBlock : class;

/// <summary>Post-action của <typeparamref name="TBlock"/>; chạy ở mọi app, trong transaction của command.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PostActionAttribute<TBlock> : Attribute
    where TBlock : class
{
    /// <summary>Post-action tùy chọn được phép tắt trong cấu hình solution; post-action bắt buộc thì không.</summary>
    public bool Optional { get; init; }
}

/// <summary>Event của <typeparamref name="TBlock"/>, phát sau khi transaction commit.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventAttribute<TBlock> : Attribute
    where TBlock : class;

/// <summary>Phiên bản hình dạng của event (K6). Bắt buộc, bắt đầu từ 1; ép bằng NVM003.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventVersionAttribute(int version) : Attribute
{
    public int Version { get; } = version;
}

/// <summary>
/// Thuộc tính bắt buộc. Trên tham số command: service layer từ chối trước khi chạy handler. Trên entity: tầng model
/// từ chối lúc <c>Submit</c>. Cột DB vẫn nullable, như nền thật.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MandatoryAttribute : Attribute;

/// <summary>Độ dài tối đa của cột chuỗi; mặc định 255. <c>-1</c> là không giới hạn.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TextLengthAttribute(int length) : Attribute
{
    public int Length { get; } = length;
}
