namespace Nvm.Foundation;

/// <summary>Đánh dấu một <see cref="AppDefinition"/>: một app công bố một phần model ra ngoài qua OData.</summary>
/// <remarks>
/// Extension app khai <see cref="BaseApp"/>: nó gắn vào base app, command của nó chạy dưới base app với tiền tố
/// <c>&lt;ExtensionApp&gt;_</c>. Extension app không gắn vào extension app khác (phẳng); một base app có nhiều extension app.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AppAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>Lớp <see cref="AppDefinition"/> của base app, nếu đây là extension app.</summary>
    public Type? BaseApp { get; init; }
}

/// <summary>Khai POM của một app: thứ gì được import thì mới đọc, gọi được qua app đó.</summary>
public abstract class AppDefinition
{
    internal void Build(PomBuilder pom) => Define(pom);

    protected abstract void Define(PomBuilder pom);
}

/// <summary>Danh sách import của POM: entity, facet, reading model, command của FB, reading function.</summary>
public sealed class PomBuilder
{
    internal HashSet<Type> Objects { get; } = [];

    internal HashSet<Type> Commands { get; } = [];

    internal HashSet<Type> ReadingFunctions { get; } = [];

    /// <summary>Import entity, facet hoặc reading model: thành entity set trên OData của app.</summary>
    public PomBuilder Entity<T>()
        where T : class
    {
        Objects.Add(typeof(T));
        return this;
    }

    /// <summary>Import command của FB: gọi được bằng POST tới tên command trên OData của app.</summary>
    public PomBuilder Command<T>()
        where T : class
    {
        Commands.Add(typeof(T));
        return this;
    }

    public PomBuilder ReadingFunction<T>()
        where T : class
    {
        ReadingFunctions.Add(typeof(T));
        return this;
    }
}

/// <summary>Người gọi: tên và role lấy từ token. Event handler và signal rule chạy dưới <see cref="System"/>.</summary>
public sealed record CallerIdentity(string User, IReadOnlySet<string> Roles)
{
    public static CallerIdentity System { get; } = new("system", new HashSet<string>(StringComparer.Ordinal));

    public bool IsInRole(string role) => Roles.Contains(role);
}

/// <summary>Reading model: lớp chỉ đọc ánh xạ vào view SQL cùng tên (<c>dbo.&lt;Tên lớp&gt;</c>).</summary>
/// <remarks>View không do Update Database tạo: nó là phần cài tay ngoài gói, nên có thể thiếu sau một lần deploy.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReadingModelAttribute<TBlock> : Attribute
    where TBlock : class;

/// <summary>Reading function của một app: chỉ đọc, trả query trì hoãn để OData lọc tiếp rồi mới chạy.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReadingFunctionAttribute(string app) : Attribute
{
    public string App { get; } = app;
}

/// <summary>Platform của reading function: chỉ đọc ngoài transaction.</summary>
public interface IReadingPlatform
{
    /// <summary>Entity, facet hoặc reading model; chỉ bản đã commit.</summary>
    IQueryable<T> ProjectionQuery<T>()
        where T : class;

    CallerIdentity Caller { get; }

    TimeProvider Time { get; }
}

public interface IReadingFunction<in TParameters, out TRow>
{
    /// <summary>Trả query chưa chạy. Nền áp <c>$filter</c>, <c>$top</c>… của lời gọi rồi mới đọc DB.</summary>
    IQueryable<TRow> Read(TParameters parameters, IReadingPlatform platform);
}

/// <summary>Handler của event, chạy ở worker role sau khi transaction phát event đã commit, ngoài response.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventSubscriberAttribute<TBlock> : Attribute
    where TBlock : class
{
    /// <summary>Tên subscription. Không có subscription đang bật thì event vẫn phát nhưng không ai nhận.</summary>
    public required string Subscription { get; init; }

    /// <summary>Subscription khai sẵn trong model, bật ở mọi plant. Không thì phải tạo theo plant (dữ liệu).</summary>
    public bool Predefined { get; init; }

    public string WorkerRole { get; init; } = "Default";
}

public interface IEventSubscriber<in TEvent>
    where TEvent : IFoundationEvent
{
    void Handle(TEvent domainEvent, IPlatform platform);
}

/// <summary>Đối tượng có trạng thái theo một state machine dạng dữ liệu.</summary>
public interface IHasStatus
{
    string? Status { get; set; }
}
