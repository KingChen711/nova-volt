namespace Nvm.Foundation.Runtime;

/// <summary>Cấu hình của solution cho một plant: command settings của từng app, worker role, các biến thể hành vi.</summary>
/// <remarks>App nào công bố gì là POM, khai trong model (<see cref="AppDefinition"/>), không nằm ở đây.</remarks>
public sealed class SolutionConfiguration
{
    /// <summary>Plant của lần deploy này: một plant một DB MOM (K3), nằm trong URL và tên exchange.</summary>
    public string Plant { get; set; } = "NV1";

    public IList<AppConfiguration> Apps { get; } = [];

    /// <summary>Role được gọi mọi command của mọi app. Rỗng: không role nào có quyền đó.</summary>
    public string? AdministratorRole { get; set; }

    /// <summary>Post-action tùy chọn đã tắt (theo tên lớp). Post-action bắt buộc thì không tắt được.</summary>
    public ISet<string> DisabledPostActions { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Worker role theo tên. Role mà handler khai nhưng không có ở đây chạy một instance, song song.</summary>
    public IDictionary<string, WorkerRoleConfiguration> WorkerRoles { get; } =
        new Dictionary<string, WorkerRoleConfiguration>(StringComparer.Ordinal);

    /// <summary>Response trả gì khi commit hỏng sau khi handler xong. Mặc định báo lỗi; biến thể kia dành cho lab L02.</summary>
    public CommitFailureReporting CommitFailureReporting { get; set; } = CommitFailureReporting.Failure;

    /// <summary>
    /// Command con báo lỗi mà handler cha vẫn trả thành công: có hủy cả transaction không. Chưa xác minh trên hệ thật, nên
    /// là cấu hình (scope §7); mặc định là không, kết quả do response của command gốc quyết định.
    /// </summary>
    public bool ChildFailureDoomsTransaction { get; set; }

    /// <summary>Endpoint của hệ ngoài theo tên (vd chuỗi kết nối LineDB). Thuộc môi trường, không thuộc model hay gói.</summary>
    public IDictionary<string, string> Endpoints { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Command settings của app; app chưa cấu hình thì không bật pre-check nào.</summary>
    public AppConfiguration App(string name) =>
        Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal)) ?? new AppConfiguration(name);

    public WorkerRoleConfiguration WorkerRole(string name) =>
        WorkerRoles.TryGetValue(name, out var role) ? role : new WorkerRoleConfiguration();
}

public enum CommitFailureReporting
{
    /// <summary>Commit hỏng thì response thất bại.</summary>
    Failure = 1,

    /// <summary>Response giữ kết quả của handler (có thể <c>Succeeded</c>); lỗi commit chỉ thấy trong trace. Lab L02.</summary>
    HandlerResult = 2,
}

/// <summary>Command settings của một app: các pre-check bật ở app đó.</summary>
public sealed class AppConfiguration(string name)
{
    public string Name { get; } = name;

    /// <summary>Pre-check bật ở app này (theo tên lớp). Cùng command gọi qua app khác thì pre-check không chạy.</summary>
    public ISet<string> EnabledPreChecks { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Bật: người gọi phải có một role ghi cho command ở <see cref="CommandRoles"/> (hoặc role quản trị); command không ghi
    /// gì thì chỉ quản trị gọi được. Tắt: ai có token hợp lệ cũng gọi được mọi command app công bố.
    /// </summary>
    public bool RestrictCommands { get; set; }

    /// <summary>Role được gọi từng command, theo tên công bố trên app (kể cả tiền tố của extension app).</summary>
    public IDictionary<string, ISet<string>> CommandRoles { get; } = new Dictionary<string, ISet<string>>(StringComparer.Ordinal);

    /// <summary>Cho <paramref name="roles"/> gọi các command <paramref name="commands"/>.</summary>
    public AppConfiguration Allow(IEnumerable<string> commands, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (var command in commands)
        {
            if (!CommandRoles.TryGetValue(command, out var set))
            {
                CommandRoles[command] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.UnionWith(roles);
        }

        return this;
    }
}

/// <summary>Worker role: số instance nhận event, và nhận tuần tự (một message một lúc) hay song song.</summary>
public sealed class WorkerRoleConfiguration
{
    public int Instances { get; set; } = 1;

    /// <summary>Tuần tự: một consumer, một message một lúc, đúng thứ tự trong queue. Bỏ qua <see cref="Instances"/>.</summary>
    public bool Sequential { get; set; }
}
